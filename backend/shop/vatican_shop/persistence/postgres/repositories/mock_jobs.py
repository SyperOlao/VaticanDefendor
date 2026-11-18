from datetime import datetime, timedelta, timezone
from uuid import uuid4

from sqlalchemy import exists, select
from ..tables import (MockOrderState, MockFulfillmentJob, PaymentOrder, PurchaseTransaction, EntitlementGrant,
                       PaymentAttempt)
from ....application.errors import ApplicationError
from ....domain.mock_payments import (OrderStatus, ProviderStatus, FulfillmentStatus, UnitStatus,
                                      TEST_ENVIRONMENT, MAX_ATTEMPTS, transaction_id)


def now():
    return datetime.now(timezone.utc)


def delay(attempts):
    return timedelta(seconds=min(300, 2 ** min(attempts, 8)))


class MockJobs:
    def claim_verification(self):
        state = self.session.scalar(select(MockOrderState).where(MockOrderState.verify_enabled.is_(True),
            MockOrderState.available_at <= now()).order_by(MockOrderState.available_at, MockOrderState.order_id)
            .limit(1).with_for_update(skip_locked=True))
        if state is None:
            return None
        if state.attempts >= MAX_ATTEMPTS:
            state.verify_enabled, state.verify_pending, state.lease_token = False, True, None
            state.review_code = state.last_error = 'VerificationExhausted'
            state.version += 1
            return {'exhausted': True}
        state.lease_token, state.available_at = uuid4(), now() + timedelta(seconds=30)
        state.attempts += 1;state.verify_pending = True;state.version += 1
        order = self.order(state.order_id)
        return {**order, 'lease': state.lease_token}

    def verification_failure(self, job, permanent=False):
        self.order(job['orderId'], lock=True)
        state = self.session.get(MockOrderState, job['orderId'])
        if state.lease_token != job['lease']:
            return False
        state.lease_token = None;state.verify_pending = True
        state.last_error = 'ProviderVerificationRejected' if permanent else 'MockProviderUnavailable'
        if permanent or state.attempts >= MAX_ATTEMPTS:
            state.verify_enabled = False;state.review_code = state.last_error
        else:
            state.available_at = now() + delay(state.attempts)
        order = self.session.get(PaymentOrder, state.order_id)
        if order.status == OrderStatus.PENDING:
            order.status = OrderStatus.UNKNOWN
        state.version += 1
        return True

    def apply_verification(self, job, truth):
        row = self.order(job['orderId'], lock=True)
        state = self.session.get(MockOrderState, job['orderId'])
        if state.lease_token != job['lease']:
            return False
        order = self.session.get(PaymentOrder, job['orderId'])
        version, status = truth['providerVersion'], truth['status']
        if version < state.last_provider_version:
            state.lease_token = None;state.available_at = now() + timedelta(seconds=5)
            return False
        if version == state.last_provider_version and state.last_provider_status != status:
            self.verification_failure(job, permanent=True)
            return False
        previous = state.last_provider_status
        if (previous == ProviderStatus.REFUNDED and status != previous
                or previous == ProviderStatus.DECLINED and status != previous
                or previous == ProviderStatus.VERIFIED and status not in (ProviderStatus.VERIFIED, ProviderStatus.REFUNDED)):
            self.verification_failure(job, permanent=True)
            return False
        state.lease_token = None;state.last_provider_version = version;state.last_provider_status = status
        state.last_error = None;state.review_code = None;state.version += 1
        self.session.add(PaymentAttempt(order_id=order.id, external_payment_id=truth['transactionId'], status=status))
        if status == ProviderStatus.PENDING:
            state.verify_pending = True;state.available_at = now() + delay(state.attempts)
            if state.attempts >= MAX_ATTEMPTS:
                state.verify_enabled = False;state.review_code = 'VerificationExhausted'
            return True
        state.attempts = 0;state.verify_pending = False
        state.available_at = now() + timedelta(seconds=60)
        if status == ProviderStatus.DECLINED:
            order.status = OrderStatus.DECLINED;state.verify_enabled = False
            return True
        identity = transaction_id(order.id)
        transaction = self.session.scalar(select(PurchaseTransaction).where(PurchaseTransaction.provider_id == order.provider_id,
            PurchaseTransaction.environment == TEST_ENVIRONMENT, PurchaseTransaction.external_transaction_id == truth['transactionId']))
        if transaction is not None and (transaction.order_id != order.id or transaction.id != identity):
            raise ApplicationError(409, 'MOCK transaction is bound to another order')
        if transaction is None:
            transaction = PurchaseTransaction(id=identity, order_id=order.id, provider_id=order.provider_id,
                environment=TEST_ENVIRONMENT, external_transaction_id=truth['transactionId'], verification_status='VERIFIED', verified_at=now())
            self.session.add(transaction);self.session.flush()
        state.transaction_id = identity
        if status == ProviderStatus.REFUNDED:
            order.status = OrderStatus.REFUNDED;transaction.verification_status = 'REFUNDED'
            transaction.refunded_at = transaction.refunded_at or now();state.verify_enabled = False
            jobs = list(self.session.scalars(select(MockFulfillmentJob).where(MockFulfillmentJob.order_id == order.id)))
            dispatched = any(item.attempts or item.receipt_json is not None for item in jobs)
            for item in jobs:
                if not item.attempts and item.state == UnitStatus.PENDING:
                    item.state = UnitStatus.CANCELLED
            state.fulfillment_status = FulfillmentStatus.REVIEW if dispatched else FulfillmentStatus.CANCELLED
            state.review_code = 'RefundAfterDispatch' if dispatched else None
            return True
        if transaction.verification_status == 'REFUNDED':
            self.verification_failure(job, permanent=True)
            return False
        if order.status != OrderStatus.FULFILLED:
            order.status = OrderStatus.VERIFIED
        for line in state.snapshot['rewardSnapshot']['items']:
            for unit in range(line['quantity']):
                key = (order.id, line['itemIndex'], unit)
                if self.session.get(MockFulfillmentJob, key) is None:
                    self.session.add(MockFulfillmentJob(order_id=order.id, line_index=line['itemIndex'], unit_index=unit,
                        transaction_id=identity, configuration_id=line['rewardRef']))
        self.session.flush();self.finalize_mock(order.id)
        return True

    def finalize_mock(self, identity):
        state = self.session.get(MockOrderState, identity)
        order = self.session.get(PaymentOrder, identity)
        jobs = list(self.session.scalars(select(MockFulfillmentJob).where(MockFulfillmentJob.order_id == identity)))
        for line in state.snapshot['rewardSnapshot']['items']:
            units = [job for job in jobs if job.line_index == line['itemIndex']]
            if len(units) == line['quantity'] and all(job.state == UnitStatus.COMPLETED for job in units):
                if self.session.scalar(select(EntitlementGrant.id).where(EntitlementGrant.transaction_id == state.transaction_id,
                        EntitlementGrant.reward_line_index == line['itemIndex'])) is None:
                    self.session.add(EntitlementGrant(transaction_id=state.transaction_id, reward_line_index=line['itemIndex'],
                        reward_type=line['rewardType'], reward_ref=line['rewardRef'], amount=line['quantity']))
        if order.status == OrderStatus.REFUNDED:
            return
        if not jobs and order.status not in (OrderStatus.VERIFIED,OrderStatus.FULFILLED):
            state.fulfillment_status = FulfillmentStatus.NOT_READY
            return
        completed = sum(job.state == UnitStatus.COMPLETED for job in jobs)
        if any(job.state == UnitStatus.REVIEW for job in jobs):
            state.fulfillment_status = FulfillmentStatus.REVIEW;state.review_code = 'EntitlementRequiresReview'
        elif jobs and completed == len(jobs) and not state.verify_pending:
            state.fulfillment_status = FulfillmentStatus.COMPLETED;order.status = OrderStatus.FULFILLED
        else:
            state.fulfillment_status = FulfillmentStatus.PARTIAL if completed else FulfillmentStatus.PENDING

    def claim_fulfillment(self):
        eligible = exists(select(MockFulfillmentJob.order_id).where(MockFulfillmentJob.order_id == MockOrderState.order_id,
            MockFulfillmentJob.state == UnitStatus.PENDING, MockFulfillmentJob.available_at <= now()))
        state = self.session.scalar(select(MockOrderState).join(PaymentOrder, PaymentOrder.id == MockOrderState.order_id)
            .where(PaymentOrder.status == OrderStatus.VERIFIED, MockOrderState.verify_pending.is_(False), eligible,
                   MockOrderState.fulfillment_status.in_((FulfillmentStatus.PENDING, FulfillmentStatus.PARTIAL)))
            .order_by(MockOrderState.order_id).limit(1).with_for_update(of=MockOrderState, skip_locked=True))
        if state is None:
            return None
        job = self.session.scalar(select(MockFulfillmentJob).where(MockFulfillmentJob.order_id == state.order_id,
            MockFulfillmentJob.state == UnitStatus.PENDING, MockFulfillmentJob.available_at <= now())
            .order_by(MockFulfillmentJob.line_index, MockFulfillmentJob.unit_index).limit(1).with_for_update())
        if job.attempts >= MAX_ATTEMPTS:
            job.state = UnitStatus.REVIEW;job.lease_token = None;job.last_error = 'EntitlementUnconfirmed'
            self.finalize_mock(state.order_id);state.version += 1
            return {'exhausted': True}
        job.lease_token = uuid4();job.attempts += 1;job.available_at = now() + timedelta(seconds=30)
        state.version += 1
        order = self.session.get(PaymentOrder, state.order_id)
        return {'orderId': order.id, 'accountId': order.account_id, 'transactionId': job.transaction_id,
                'lineIndex': job.line_index, 'unitIndex': job.unit_index, 'configurationId': job.configuration_id,
                'lease': job.lease_token}

    def finish_fulfillment(self, claim, receipt=None, permanent=False):
        self.order(claim['orderId'], lock=True)
        state = self.session.get(MockOrderState, claim['orderId'])
        job = self.session.get(MockFulfillmentJob, (claim['orderId'], claim['lineIndex'], claim['unitIndex']),
                               with_for_update=True, populate_existing=True)
        if job.state != UnitStatus.PENDING or job.lease_token != claim['lease']:
            return False
        job.lease_token = None
        if receipt is not None:
            job.state = UnitStatus.COMPLETED;job.receipt_json = receipt;job.last_error = None
        else:
            job.last_error = 'EntitlementRejected' if permanent else 'EntitlementUnconfirmed'
            if permanent or job.attempts >= MAX_ATTEMPTS:
                job.state = UnitStatus.REVIEW
            else:
                job.available_at = now() + delay(job.attempts)
        self.session.flush();self.finalize_mock(claim['orderId']);state.version += 1
        return True

    def retry_mock(self, identity, version):
        row = self.order(identity, lock=True)
        if row is None:
            raise ApplicationError(404, 'MOCK order not found')
        state = self.session.get(MockOrderState, identity)
        if state.version != version or row['status'] in (OrderStatus.REFUNDED, OrderStatus.DECLINED):
            raise ApplicationError(409, 'MOCK order cannot be retried at this version')
        if state.review_code is None and state.fulfillment_status != FulfillmentStatus.REVIEW:
            raise ApplicationError(409, 'MOCK order is not in Review')
        for job in self.session.scalars(select(MockFulfillmentJob).where(MockFulfillmentJob.order_id == identity,
                MockFulfillmentJob.state == UnitStatus.REVIEW)):
            job.state = UnitStatus.PENDING;job.attempts = 0;job.lease_token = None;job.available_at = now();job.last_error = None
        state.verify_enabled = state.verify_pending = True
        state.attempts = 0;state.available_at = now();state.lease_token = None;state.review_code = state.last_error = None
        state.version += 1
        self.session.flush();self.finalize_mock(identity)
