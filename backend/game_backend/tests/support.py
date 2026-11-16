"""Helpers for the current ORM and use-case tests."""
from concurrent.futures import ThreadPoolExecutor
import time
from uuid import NAMESPACE_URL, UUID, uuid4, uuid5

from vatican_backend.persistence.postgres.tables import RewardBundle, PaymentProvider, Storefront, ShopProduct, PaymentOrder


def server_header(slot):
    return {"X-Server-Secret": slot.secret}


def assigned_run(client, slot, player):
    assert client.post(f"/internal/servers/{slot.server_id}/ready", headers=server_header(slot),
                       json={"contentReleaseId": "sha256-test-release", "protocolVersion": 3}).status_code == 200
    with ThreadPoolExecutor(max_workers=1) as pool:
        future = pool.submit(client.app.state.use_cases.runs.assign, UUID(player["accountId"]))
        pending = None
        for _ in range(60):
            response = client.get(f"/internal/servers/{slot.server_id}/assignment",
                                  headers=server_header(slot))
            if response.json()["assigned"]:
                pending = response.json()
                break
            time.sleep(0.05)
        assert pending is not None
        assert client.post(f"/internal/servers/{slot.server_id}/ack",
                           headers=server_header(slot),
                           json={"runId": pending["runId"],
                                 "generation": pending["generation"]}).status_code == 200
        assert future.result(timeout=5)["runId"] == pending["runId"]
    return pending


def result_body(pending, outcome="victory"):
    return {"runId": pending["runId"], "accountId": pending["accountId"],
            "contentReleaseId": pending["contentReleaseId"],
            "generation": pending["generation"], "outcome": outcome,
            "churchLevel": 1}


def payment_order(database):
    account_id, bundle_id, product_id, order_id = (uuid4() for _ in range(4))
    with database.transaction() as session:
        session.add(RewardBundle(id=bundle_id, code="gems", version=1))
        session.add(PaymentProvider(id=1, code="MOCK", provider_kind="TEST"))
        session.add(Storefront(id=1, code="WEB", platform="WEB"))
        session.flush()
        session.add(ShopProduct(id=product_id, internal_sku="gems.pack", version=1,
                                reward_bundle_id=bundle_id, product_type="CONSUMABLE"))
        session.flush()
        session.add(PaymentOrder(id=order_id, account_id=account_id, product_id=product_id,
                                 storefront_id=1, provider_id=1, environment="TEST", client_request_key=uuid4(),
                                 currency_iso="USD", price_minor=199))
    return order_id


def game_app(settings):
    """Create an app with a known local profile for direct use-case tests."""
    from vatican_backend.api import create_app

    account_id = uuid5(NAMESPACE_URL, settings.database_url)
    app = create_app(settings)
    app.state.test_player = {"accountId": str(account_id)}
    return app


def player_for(client):
    return client.app.state.test_player
