from uuid import uuid4
import pytest
from sqlalchemy.exc import IntegrityError
from vatican_shop.persistence.postgres.tables import (EntitlementGrant, PurchaseTransaction, ProviderNotification,
    PaymentOrder, PaymentProvider, OutboxMessage, Storefront, ShopProduct)


def payment_order(database):
    order_id, product_id = uuid4(), uuid4()
    with database.transaction() as session:
        session.add(PaymentProvider(id=1, code="MOCK", provider_kind="TEST"))
        session.add(Storefront(id=1, code="WEB", platform="WEB"))
        session.flush()
        session.add(ShopProduct(id=product_id, internal_sku="gems.pack", version=1,
            reward_bundle_id=uuid4(), product_type="CONSUMABLE"))
        session.flush()
        session.add(PaymentOrder(id=order_id, account_id=uuid4(), product_id=product_id,
            storefront_id=1, provider_id=1, environment="TEST", client_request_key=uuid4(),
            currency_iso="USD", price_minor=199))
    return order_id


def test_payment_transaction_and_grant_have_distinct_idempotency_keys(shop_storage):
    database, _ = shop_storage
    order_id = payment_order(database)
    transaction_id = uuid4()
    with database.transaction() as session:
        session.add(PurchaseTransaction(id=transaction_id, order_id=order_id, provider_id=1, environment="TEST",
                                         external_transaction_id="store-1", verification_status="VERIFIED"))
    with pytest.raises(IntegrityError):
        with database.transaction() as session:
            session.add(PurchaseTransaction(order_id=order_id, provider_id=1, environment="TEST",
                                             external_transaction_id="store-1", verification_status="VERIFIED"))
    # Another store transaction for the same consumable product is legitimate.
    with database.transaction() as session:
        session.add(PurchaseTransaction(order_id=order_id, provider_id=1, environment="TEST",
                                         external_transaction_id="store-2", verification_status="VERIFIED"))
        session.add(EntitlementGrant(transaction_id=transaction_id, reward_line_index=0,
                                     reward_type="CURRENCY", reward_ref="GEMS", amount=100))
    with pytest.raises(IntegrityError):
        with database.transaction() as session:
            session.add(EntitlementGrant(transaction_id=transaction_id, reward_line_index=0,
                                         reward_type="CURRENCY", reward_ref="GEMS", amount=100))

def test_duplicate_notification_is_scoped_to_provider_environment(shop_storage):
    database, _ = shop_storage
    payment_order(database)
    with database.transaction() as session:
        session.add(ProviderNotification(provider_id=1, environment="TEST", external_event_id="event-1", payload_hash=b"x" * 32))
        session.add(ProviderNotification(provider_id=1, environment="PRODUCTION", external_event_id="event-1", payload_hash=b"x" * 32))
    with pytest.raises(IntegrityError):
        with database.transaction() as session:
            session.add(ProviderNotification(provider_id=1, environment="TEST", external_event_id="event-1", payload_hash=b"x" * 32))

def test_payment_order_provider_environment_and_outbox_are_linked(shop_storage):
    database, _ = shop_storage
    order_id = payment_order(database)
    with database.transaction() as session:
        session.add(PaymentProvider(id=2, code="OTHER", provider_kind="STORE"))
        session.get(PaymentOrder, order_id).status = "UNKNOWN"
    for provider_id, environment in [(2, "TEST"), (1, "PRODUCTION")]:
        with pytest.raises(IntegrityError):
            with database.transaction() as session:
                session.add(PurchaseTransaction(order_id=order_id, provider_id=provider_id,
                    environment=environment, external_transaction_id="external", verification_status="VERIFIED"))
    transaction_id = uuid4()
    with database.transaction() as session:
        session.add(PurchaseTransaction(id=transaction_id, order_id=order_id, provider_id=1,
            environment="TEST", external_transaction_id="external", verification_status="VERIFIED"))
    with pytest.raises(IntegrityError):
        with database.transaction() as session:
            session.add(OutboxMessage(destination="payments", event_name="purchase_verified", schema_version=1,
                aggregate_id=uuid4(), payment_transaction_id=transaction_id, payload={}))
    with database.transaction() as session:
        session.add(OutboxMessage(destination="payments", event_name="purchase_verified", schema_version=1,
            aggregate_id=transaction_id, payment_transaction_id=transaction_id, payload={}))

