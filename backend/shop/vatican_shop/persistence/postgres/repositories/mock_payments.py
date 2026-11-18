from datetime import datetime, timezone
from uuid import UUID, uuid4

from sqlalchemy import func, select

from .mock_jobs import MockJobs
from ..tables import (MockProduct, MockOrderState, MockProviderPayment, MockFulfillmentJob, PaymentOrder,
    PaymentProvider, Storefront, ShopProduct, StoreProductMapping, ProviderNotification, IdempotencyRecord)
from ....application.errors import ApplicationError
from ....domain.mock_payments import (MOCK_PROVIDER, MOCK_STOREFRONT, TEST_ENVIRONMENT, ProviderStatus,
    Scenario, external_transaction_id)


def now():
    return datetime.now(timezone.utc)


class MockPaymentsRepository(MockJobs):
    def __init__(self, session):
        self.session = session

    def lock(self, key):
        self.session.execute(select(func.pg_advisory_xact_lock(func.hashtextextended('shop.mock:' + str(key), 0))))

    def command(self, actor, operation, key, payload_hash):
        row = self.session.get(IdempotencyRecord, (actor, operation, str(key)))
        if row is None:
            return None
        if row.payload_hash != payload_hash:
            raise ApplicationError(409, 'MOCK command payload changed')
        return row.result_json

    def record_command(self, actor, operation, key, payload_hash, result):
        self.session.add(IdempotencyRecord(account_id=actor, operation=operation, request_key=str(key),
            payload_hash=payload_hash, status='COMPLETED', result_json=result, completed_at=now()))

    def routing(self):
        self.lock('configuration')
        provider = self.session.scalar(select(PaymentProvider).where(PaymentProvider.code == MOCK_PROVIDER))
        storefront = self.session.scalar(select(Storefront).where(Storefront.code == MOCK_STOREFRONT))
        if provider is None:
            provider = PaymentProvider(id=(self.session.scalar(select(func.max(PaymentProvider.id))) or 0) + 1,
                                       code=MOCK_PROVIDER, provider_kind='MOCK', enabled=True)
            self.session.add(provider)
        if storefront is None:
            storefront = Storefront(id=(self.session.scalar(select(func.max(Storefront.id))) or 0) + 1,
                                    code=MOCK_STOREFRONT, platform='WEB', enabled=True)
            self.session.add(storefront)
        self.session.flush()
        if provider.provider_kind != 'MOCK' or storefront.platform != 'WEB':
            raise ApplicationError(409, 'Reserved MOCK routing conflicts with existing configuration')
        return provider, storefront

    def product(self, identity):
        result = self.session.execute(select(MockProduct, ShopProduct, StoreProductMapping, PaymentProvider, Storefront)
            .join(ShopProduct, ShopProduct.id == MockProduct.product_id)
            .join(StoreProductMapping, StoreProductMapping.id == MockProduct.mapping_id)
            .join(PaymentProvider, PaymentProvider.id == StoreProductMapping.provider_id)
            .join(Storefront, Storefront.id == StoreProductMapping.storefront_id)
            .where(MockProduct.product_id == identity)).one_or_none()
        if result is None:
            return None
        mock, product, mapping, provider, storefront = result
        if (provider.code != MOCK_PROVIDER or provider.provider_kind != 'MOCK' or storefront.code != MOCK_STOREFRONT
                or mapping.environment != TEST_ENVIRONMENT):
            raise ApplicationError(409, 'Invalid MOCK product routing')
        return {**mock.snapshot, 'enabled': product.enabled and provider.enabled and storefront.enabled,
                'configurationHash': mock.configuration_hash, 'providerId': provider.id, 'storefrontId': storefront.id}

    def products(self, limit, after):
        query = select(MockProduct.product_id).order_by(MockProduct.product_id).limit(limit)
        if after is not None:
            query = query.where(MockProduct.product_id > after)
        return [self.product(identity) for identity in self.session.scalars(query)]

    def publish_product(self, snapshot, payload_hash):
        provider, storefront = self.routing()
        identity = UUID(snapshot['productId'])
        prior = self.product(identity)
        if prior is not None:
            if prior['configurationHash'] != payload_hash:
                raise ApplicationError(409, 'MOCK product version is immutable')
            return prior
        if self.session.scalar(select(ShopProduct.id).where(ShopProduct.internal_sku == snapshot['sku'],
                ShopProduct.version == snapshot['version'])) is not None:
            raise ApplicationError(409, 'Product SKU/version already exists')
        if self.session.scalar(select(StoreProductMapping.id).where(StoreProductMapping.provider_id == provider.id,
                StoreProductMapping.storefront_id == storefront.id, StoreProductMapping.environment == TEST_ENVIRONMENT,
                StoreProductMapping.external_sku == snapshot['externalSku'])) is not None:
            raise ApplicationError(409, 'MOCK external SKU is already bound; use a new versioned SKU')
        product = ShopProduct(id=identity, internal_sku=snapshot['sku'], version=snapshot['version'],
            reward_bundle_id=UUID(snapshot['rewardBundleId']), product_type='CONSUMABLE', enabled=True)
        self.session.add(product);self.session.flush()
        mapping = StoreProductMapping(id=uuid4(), product_id=identity, storefront_id=storefront.id,
            provider_id=provider.id, external_sku=snapshot['externalSku'], environment=TEST_ENVIRONMENT)
        self.session.add(mapping);self.session.flush()
        self.session.add(MockProduct(product_id=identity, mapping_id=mapping.id,
                                    configuration_hash=payload_hash, snapshot=snapshot))
        self.session.flush()
        return self.product(identity)

    def availability(self, identity, enabled):
        if self.product(identity) is None:
            raise ApplicationError(404, 'MOCK product not found')
        row = self.session.get(ShopProduct, identity, with_for_update=True)
        row.enabled = enabled

    def by_key(self, account, key):
        identity = self.session.scalar(select(PaymentOrder.id)
            .where(PaymentOrder.account_id == account, PaymentOrder.client_request_key == key))
        if identity is None:
            return None
        prior = self.order(identity)
        if prior is None:
            raise ApplicationError(409, 'Request key is already bound to another payment order')
        return prior

    def order(self, identity, lock=False):
        query = select(PaymentOrder, MockOrderState).join(MockOrderState, MockOrderState.order_id == PaymentOrder.id)
        if lock:
            query = query.with_for_update().execution_options(populate_existing=True)
        result = self.session.execute(query.where(PaymentOrder.id == identity)).one_or_none()
        if result is None:
            return None
        order, state = result
        return {'orderId': order.id, 'accountId': order.account_id, 'requestKey': order.client_request_key,
                'providerId': order.provider_id, 'status': order.status, 'snapshot': dict(state.snapshot),
                'payloadHash': state.payload_hash, 'fulfillmentStatus': state.fulfillment_status,
                'transactionId': state.transaction_id, 'version': state.version, 'reviewCode': state.review_code,
                'providerVersion': state.last_provider_version, 'createdAt': order.created_at.isoformat()}

    def units(self, identity):
        return [{'lineIndex': row.line_index, 'unitIndex': row.unit_index, 'state': row.state,
                 'attempts': row.attempts, 'lastError': row.last_error, 'receipt': row.receipt_json}
                for row in self.session.scalars(select(MockFulfillmentJob).where(MockFulfillmentJob.order_id == identity)
                    .order_by(MockFulfillmentJob.line_index, MockFulfillmentJob.unit_index))]

    def create_order(self, account, key, product, payload_hash):
        identity = uuid4()
        snapshot = {k: v for k, v in product.items() if k not in ('configurationHash', 'enabled', 'providerId', 'storefrontId')}
        self.session.add(PaymentOrder(id=identity, account_id=account,
            product_id=UUID(product['productId']), storefront_id=product['storefrontId'],
            provider_id=product['providerId'], environment=TEST_ENVIRONMENT, client_request_key=key,
            currency_iso=product['currencyIso'], price_minor=product['priceMinor']))
        self.session.flush()
        self.session.add(MockOrderState(order_id=identity, snapshot=snapshot, payload_hash=payload_hash))
        self.session.add(MockProviderPayment(order_id=identity, truth_json={
            'orderId': str(identity), 'accountId': str(account), 'environment': TEST_ENVIRONMENT,
            'transactionId': external_transaction_id(identity), 'externalSku': snapshot['externalSku'],
            'priceMinor': snapshot['priceMinor'], 'currencyIso': snapshot['currencyIso'],
            'status': ProviderStatus.PENDING, 'providerVersion': 1}))
        self.session.flush()
        return self.order(identity)

    def provider(self, identity, lock=False):
        row = self.session.get(MockProviderPayment, identity, with_for_update=lock, populate_existing=True)
        if row is None:
            raise ApplicationError(404, 'MOCK provider payment not found')
        return dict(row.truth_json), row.unavailable

    def simulate(self, identity, scenario, unavailable):
        truth, _ = self.provider(identity, lock=True)
        if scenario != Scenario.DUPLICATE_WEBHOOK:
            target = {Scenario.SUCCESS: ProviderStatus.VERIFIED, Scenario.TIMEOUT: ProviderStatus.VERIFIED,
                      Scenario.DECLINED: ProviderStatus.DECLINED, Scenario.PENDING: ProviderStatus.PENDING,
                      Scenario.REFUND: ProviderStatus.REFUNDED}[scenario]
            previous = truth['status']
            if (previous == ProviderStatus.REFUNDED and target != previous
                    or previous == ProviderStatus.DECLINED and target != previous
                    or previous == ProviderStatus.VERIFIED and target not in (ProviderStatus.VERIFIED, ProviderStatus.REFUNDED)
                    or target == ProviderStatus.REFUNDED and previous not in (ProviderStatus.VERIFIED, ProviderStatus.REFUNDED)):
                raise ApplicationError(409, 'Invalid MOCK provider state transition')
            if truth['providerVersion'] >= 2_147_483_647:
                raise ApplicationError(409, 'MOCK provider version exhausted')
            truth = {**truth, 'status': target, 'providerVersion': truth['providerVersion'] + 1}
        row = self.session.get(MockProviderPayment, identity)
        row.truth_json, row.unavailable, row.updated_at = truth, unavailable, now()
        return truth

    def signal(self, identity, event_key, payload_hash):
        self.lock('signal:' + event_key)
        order = self.order(identity)
        if order is None:
            raise ApplicationError(404, 'MOCK order not found')
        prior = self.session.scalar(select(ProviderNotification).where(
            ProviderNotification.provider_id == order['providerId'], ProviderNotification.environment == TEST_ENVIRONMENT,
            ProviderNotification.external_event_id == event_key))
        if prior is not None:
            if prior.payload_hash != payload_hash:
                raise ApplicationError(409, 'MOCK event payload changed')
            return False
        self.order(identity, lock=True)
        state = self.session.get(MockOrderState, identity)
        self.session.add(ProviderNotification(provider_id=order['providerId'], environment=TEST_ENVIRONMENT,
            external_event_id=event_key, payload_hash=payload_hash, status='ACCEPTED', processed_at=now()))
        state.verify_enabled, state.verify_pending = True, True
        state.available_at, state.attempts, state.lease_token = now(), 0, None
        state.version += 1
        return True
