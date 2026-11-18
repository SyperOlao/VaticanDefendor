"""Boundaries that must remain closed even with valid TEST credentials."""
from uuid import UUID, uuid4

import httpx
import pytest
from fastapi.testclient import TestClient

from vatican_shop.application.errors import ApplicationError
from vatican_shop.bootstrap.settings import Settings
from vatican_shop.domain.mock_payments import MockPolicy, grant_payload
from vatican_shop.integrations.mock_payments import RewardBundlesHttpGateway
from vatican_shop.persistence.postgres.tables import MockProduct, PurchaseTransaction
from vatican_inventory.application.errors import ApplicationError as InventoryError
from vatican_inventory.persistence.postgres.tables import MockPaymentReceipt
from vatican_backend.application.level_rewards import PublishRewardBundle
from vatican_backend.bootstrap.settings import Settings as GameSettings
from vatican_backend.domain.level_rewards import RewardPlan
from vatican_backend.presentation.app import create_app as game_app
from .test_mock_payments import mock_world, order, simulate, drain, count


def test_published_bundle_http_contract_can_be_used_by_shop_without_creating_a_profile(mock_world):
    w = mock_world
    product, content = w.publish()
    from vatican_backend.bootstrap.weapon_catalog import LoadCatalog
    from vatican_backend.persistence.postgres.tables import WeaponConfiguration
    with w.game.transaction() as session:
        for definition in LoadCatalog():
            existing = session.get(WeaponConfiguration, definition['Id'])
            if existing is not None:
                existing.configuration_json = definition
    with TestClient(game_app(GameSettings(w.game_url, {}, 120))) as game:
        def handle(request):
            response = game.get(request.url.path)
            return httpx.Response(response.status_code, json=response.json())
        with httpx.Client(transport=httpx.MockTransport(handle)) as client:
            gateway = RewardBundlesHttpGateway(client, Settings(w.url, 's' * 32))
            assert gateway.get(content['rewardBundleId']) == product['rewardSnapshot']
            with pytest.raises(ApplicationError) as error:
                gateway.get(uuid4())
            assert error.value.status_code == 422
        assert game.get('/v1/reward-bundles/invalid').status_code == 422
    from vatican_backend.persistence.postgres.tables import Profile
    assert count(w.game, Profile) == 0


def test_unsupported_published_reward_is_rejected_before_any_shop_product_is_created(mock_world):
    w = mock_world
    bundle = uuid4()
    plan = RewardPlan.parse({'schemaVersion': 1, 'bundleId': str(bundle), 'items': [
        {'itemIndex': 0, 'rewardType': 'CURRENCY', 'rewardRef': 'PRESTIGE', 'quantity': 10}]})
    PublishRewardBundle(w.guow).execute('unsupported_mock', 1, plan)
    with pytest.raises(ApplicationError) as error:
        w.api.publish('admin', w.policy.control_secret, {'sku': 'unsupported', 'version': 1,
            'externalSku': 'unsupported.v1', 'rewardBundleId': bundle, 'priceMinor': 100, 'currencyIso': 'USD'})
    assert error.value.status_code == 422 and count(w.db, MockProduct) == 0


def test_even_a_validly_signed_sdk_success_cannot_replace_provider_verification(mock_world):
    w = mock_world
    identity, _ = order(w)
    truth = w.policy.parse_receipt(w.provider.lookup(identity))
    w.api.confirm('user', identity, w.policy.receipt({**truth, 'status': 'VERIFIED'}))
    drain(w)
    assert w.api.get('user', identity)['status'] == 'PENDING'
    assert count(w.db, PurchaseTransaction) == count(w.inventory, MockPaymentReceipt) == 0


@pytest.mark.parametrize('raw', [b'{}', b'{"orderId":null}', b'\xff', b'[]',
    b'{"providerVersion":NaN}', b'{"orderId":"a","orderId":"b"}', b'[' * 1000])
def test_signed_malformed_webhooks_are_rejected_without_an_inbox_record(mock_world, raw):
    w = mock_world
    with pytest.raises(ApplicationError) as error:
        w.api.webhook(raw, w.policy.sign_webhook(raw))
    assert error.value.status_code == 422
    from vatican_shop.persistence.postgres.tables import ProviderNotification
    assert count(w.db, ProviderNotification) == 0


def test_cached_inventory_grant_still_requires_the_separate_credential(mock_world):
    w = mock_world
    identity, _ = order(w)
    simulate(w, identity)
    w.verify.once()
    with w.uow() as work:
        claim = work.mock.claim_fulfillment()
    receipt = w.delivery.grant(claim)
    w.definitions.down = True
    for credential in (None, 'bad', 'é' * 64):
        with pytest.raises(InventoryError) as error:
            w.grants.grant(grant_payload(claim), credential)
        assert error.value.status_code == 401
    assert w.grants.grant(grant_payload(claim), w.policy.grant_secret) == receipt


def test_operator_can_inspect_recovery_version_but_a_revoked_admin_cannot(mock_world):
    w = mock_world
    identity, _ = order(w)
    result = w.api.inspect('admin', w.policy.control_secret, identity)
    assert result == w.api.get('user', identity)
    w.auth.revoked = True
    with pytest.raises(ApplicationError) as error:
        w.api.inspect('admin', w.policy.control_secret, identity)
    assert error.value.status_code == 403


def test_checkout_cannot_reuse_the_key_of_another_payment_flow(mock_world):
    from vatican_shop.persistence.postgres.tables import PaymentOrder, MockOrderState
    w = mock_world
    product, _ = w.publish()
    key, old = uuid4(), uuid4()
    with w.uow() as work:
        published = work.mock.product(UUID(product['productId']))
    with w.db.transaction() as session:
        session.add(PaymentOrder(id=old, account_id=w.auth.user, client_request_key=key,
            product_id=UUID(product['productId']), provider_id=published['providerId'],
            storefront_id=published['storefrontId'], environment='TEST', currency_iso='USD', price_minor=199))
    with pytest.raises(ApplicationError) as error:
        w.api.create('user', UUID(product['productId']), key)
    assert error.value.status_code == 409
    assert count(w.db, PaymentOrder) == 1 and count(w.db, MockOrderState) == 0


@pytest.mark.parametrize('environment', ['production', 'staging', 'TEST', ''])
def test_mock_cannot_be_enabled_outside_explicit_test_or_development(environment):
    with pytest.raises(ValueError):
        MockPolicy(True, environment, 'c' * 32, 'w' * 32, 'r' * 32, 'i' * 32)


def test_enabled_mock_requires_distinct_secrets_and_strict_environment_flags(monkeypatch):
    with pytest.raises(ValueError):
        MockPolicy(True, 'test', *(['x' * 32] * 4))
    monkeypatch.setenv('VD_SERVICE_SECRET', 's' * 32)
    monkeypatch.setenv('VD_SHOP_MOCK_PAYMENTS_ENABLED', 'true')
    with pytest.raises(ValueError, match='must be 0 or 1'):
        Settings.from_environment()
