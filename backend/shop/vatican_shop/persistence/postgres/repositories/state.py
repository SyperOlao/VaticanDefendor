from datetime import datetime, timedelta, timezone
from uuid import uuid4
from sqlalchemy import func, select, tuple_
from .messaging import MessagingRepository
from .operations import OperationsQueries
from ..tables import PurchaseOperation, PurchaseJob
from ....application.catalog import ACTIVE_STATES, MAX_PURCHASE_ATTEMPTS, RESERVED_STATUSES, PurchaseEventKind, PurchaseState, ReceiptStatus
from ....application.errors import ApplicationError
from ....application.models import PurchaseRecord


def now():
    return datetime.now(timezone.utc)


class StateRepository(OperationsQueries, MessagingRepository):
    def _record(self,row):
        analytics=self.analytics_delivery(row.account_id,row.operation_id)
        receipt={**row.receipt_json,'analyticsStatus':analytics['status'],'analytics':analytics}
        return PurchaseRecord(row.operation_id,row.offer_id,row.catalog_version,receipt,row.created_at)

    def operation(self, account_id, operation_id):
        row = self.session.get(PurchaseOperation, (account_id, operation_id))
        return self._record(row) if row is not None else None

    def history(self, account_id, limit, before):
        query = select(PurchaseOperation).where(PurchaseOperation.account_id == account_id)
        if before is not None:
            cursor = self.operation(account_id, before)
            if cursor is None:
                raise ApplicationError(404, "Purchase cursor not found")
            query = query.where(tuple_(PurchaseOperation.created_at, PurchaseOperation.operation_id) <
                                (cursor.created_at, cursor.operation_id))
        rows = self.session.scalars(query.order_by(PurchaseOperation.created_at.desc(),
                                                   PurchaseOperation.operation_id.desc()).limit(limit))
        return [self._record(row) for row in rows]

    def counts(self, account_id):
        rows = self.session.execute(select(PurchaseOperation.offer_id, func.count()).where(
            PurchaseOperation.account_id == account_id,
            PurchaseOperation.receipt_json["status"].astext.in_(RESERVED_STATUSES)
        ).group_by(PurchaseOperation.offer_id))
        return dict(rows.all())

    def create(self, account_id, operation_id, offer_id, catalog_version, receipt, plan):
        self.session.add(PurchaseOperation(account_id=account_id, operation_id=operation_id,
            offer_id=offer_id, catalog_version=catalog_version, receipt_json={**receipt, "plan": plan}))
        self.session.flush()
        if receipt["status"] == ReceiptStatus.PENDING:
            self.session.add(PurchaseJob(account_id=account_id, operation_id=operation_id))
        self._event(account_id, operation_id, 1, PurchaseEventKind.CREATED,
                    PurchaseState.PENDING if receipt["status"] == ReceiptStatus.PENDING else PurchaseState.REJECTED,
                    attempts=0 if receipt["status"] == ReceiptStatus.PENDING else None, error_code=receipt.get("code"))

    def claim(self):
        row = self.session.scalar(select(PurchaseJob).where(
            PurchaseJob.state.in_(ACTIVE_STATES),
            PurchaseJob.available_at <= now()).order_by(PurchaseJob.available_at).limit(1)
            .with_for_update(skip_locked=True))
        if row is None:
            return None
        if row.attempts >= MAX_PURCHASE_ATTEMPTS:
            # An expired final lease may have applied its command before the worker crashed.
            previous = row.state
            row.state = PurchaseState.REVIEW
            row.lease_token = None
            row.version += 1
            row.last_error_code = "ReconciliationRequired"
            operation = self.session.get(PurchaseOperation, (row.account_id, row.operation_id))
            operation.receipt_json = {**operation.receipt_json, "status": ReceiptStatus.REVIEW,
                                      "code": "ReconciliationRequired", "_resumeState": previous}
            self._event(row.account_id, row.operation_id, row.version, PurchaseEventKind.REVIEW, row.state,
                        from_state=previous, attempts=row.attempts, error_code=row.last_error_code)
            return {"state": PurchaseState.REVIEW}
        row.lease_token = uuid4()
        row.available_at = now() + timedelta(seconds=30)
        row.attempts += 1
        row.version += 1
        self._event(row.account_id, row.operation_id, row.version, PurchaseEventKind.CLAIMED, row.state,
                    from_state=row.state, attempts=row.attempts)
        op = self.operation(row.account_id, row.operation_id)
        return {"accountId": row.account_id, "operationId": row.operation_id, "state": row.state,
                "lease": row.lease_token, "plan": op.receipt_json["plan"]}

    def transition(self, job, state, receipt=None, retry=False, error_code=None):
        row = self.session.get(PurchaseJob, (job["accountId"], job["operationId"]), with_for_update=True, populate_existing=True)
        if row is None or row.lease_token != job["lease"] or row.state != job["state"]:
            return False
        if state != row.state and not retry and state in ACTIVE_STATES:
            row.attempts = 0
        row.state = state
        row.lease_token = None
        row.available_at = now() + timedelta(seconds=min(300, 2 ** min(row.attempts, 8)) if retry else 0)
        if retry and row.attempts >= MAX_PURCHASE_ATTEMPTS:
            row.state = PurchaseState.REVIEW
            receipt = {"status": ReceiptStatus.REVIEW, "code": "ReconciliationRequired", "_resumeState": state}
        if receipt:
            op = self.session.get(PurchaseOperation, (row.account_id, row.operation_id))
            if state==PurchaseState.COMPLETED:
                completed_at=now()
                self.enqueue_purchase(op,receipt,completed_at)
                receipt={**receipt,'completedAt':completed_at.isoformat()}
            op.receipt_json = {**op.receipt_json, **receipt}
        row.version += 1
        row.last_error_code = receipt.get("code") if receipt else error_code
        kind = PurchaseEventKind.REVIEW if row.state == PurchaseState.REVIEW else (
            PurchaseEventKind.RETRY_SCHEDULED if retry else PurchaseEventKind(row.state))
        self._event(row.account_id, row.operation_id, row.version, kind, row.state, from_state=job["state"],
                    attempts=row.attempts, error_code=row.last_error_code)
        return True


    def requeue(self, account_id, operation_id, *, expected_version=None, actor_id=None, reason=None, request_key=None):
        job = self.session.get(PurchaseJob, (account_id, operation_id), with_for_update=True, populate_existing=True)
        if job is None or job.state != PurchaseState.REVIEW:
            return False
        if expected_version is not None and job.version != expected_version:
            return False
        operation = self.session.get(PurchaseOperation, (account_id, operation_id))
        if operation.receipt_json.get("status") != ReceiptStatus.REVIEW:
            return False
        state = operation.receipt_json.get("_resumeState")
        if state not in ACTIVE_STATES:
            return False
        job.state = state
        job.attempts = 0
        job.lease_token = None
        job.available_at = now()
        previous_error = job.last_error_code
        job.last_error_code = None
        job.version += 1
        operation.receipt_json = {k:v for k,v in operation.receipt_json.items() if k not in ("code", "_resumeState")}
        operation.receipt_json = {**operation.receipt_json, "status": ReceiptStatus.PENDING}
        self._event(account_id, operation_id, job.version,
                    PurchaseEventKind.ADMIN_RETRY if actor_id else PurchaseEventKind.CLI_RETRY, state,
                    from_state=PurchaseState.REVIEW, attempts=0, error_code=previous_error,
                    actor_id=actor_id, reason=reason, request_key=request_key)
        return True
