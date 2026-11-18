from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone
from types import SimpleNamespace
from uuid import UUID, uuid4

import pytest
import httpx
from dishka import Provider, Scope, provide
from fastapi.testclient import TestClient
from sqlalchemy import func, select

from vatican_inventory.application.use_cases import InventoryUseCases
from vatican_inventory.persistence.postgres.tables import OwnedWeapon
from vatican_inventory.persistence.postgres.repositories.state import StateRepository as InventoryRepository
from vatican_inventory.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as InventoryUow
from vatican_inventory.application.ports import DefinitionsGateway as InventoryDefinitions
from vatican_inventory.bootstrap.settings import Settings as InventorySettings
from vatican_inventory.presentation.app import create_app as inventory_app
from vatican_shop.application.errors import ApplicationError, ParticipantConflict
from vatican_shop.application.ports import DefinitionsGateway as ShopCatalog, PurchaseGateway as PurchaseGatewayPort
from vatican_shop.application.use_cases import SettlePurchases, ShopUseCases
from vatican_shop.bootstrap.settings import Settings
from vatican_shop.integrations.shop import PurchaseGateway
from vatican_shop.presentation.app import create_app
from vatican_shop.persistence.postgres.tables import PurchaseJob, PurchaseOperation
from vatican_shop.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory
from vatican_wallet.application.use_cases import WalletUseCases
from vatican_wallet.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as WalletUow
from vatican_wallet.bootstrap.settings import Settings as WalletSettings
from vatican_wallet.presentation.app import create_app as wallet_app


class Catalog:
    def __init__(self):
        self.price = 25
        self.version = "v1"
        self.limit = 0
        self.unavailable = False

    def get(self):
        if self.unavailable:
            raise ApplicationError(503, "Catalog unavailable")
        return {"version": self.version, "offers": [{"id": "sword", "weaponConfigurationId": "sword.rare",
                "price": self.price, "purchaseLimit": self.limit, "available": True, "unavailableReason": ""}]}


class Definitions:
    def __init__(self, database):
        self.database = database
        self.transaction_active = lambda: self.database.engine.pool.checkedout() != 0
        self.ids = ["sword.common", "sword.rare"]
        self.unavailable = False

    def get(self):
        assert not self.transaction_active()
        if self.unavailable:
            from vatican_inventory.application.errors import ApplicationError as InventoryError
            raise InventoryError(503, "Definitions unavailable")
        return [{"Id": key, "ExperiencePerLevel": 100, "MaximumLevel": 10} for key in self.ids]


@pytest.fixture
def world(shop_storage, wallet_storage, inventory_storage):
    database = shop_storage[0]
    uow = PostgresUnitOfWorkFactory(database)
    catalog = Catalog()
    wallet = WalletUseCases(WalletUow(wallet_storage[0]))
    definitions = Definitions(inventory_storage[0])
    inventory = InventoryUseCases(InventoryUow(inventory_storage[0]), definitions)

    class Gateway:
        lost = None
        conflict = None
        calls = []
        check_connections = True

        def invoke(self, step, job):
            if self.check_connections:
                assert database.engine.pool.checkedout() == 0
            self.calls.append(step)
            if self.conflict == step:
                raise ParticipantConflict()
            account, operation = job["accountId"], job["operationId"]
            if step == "grant":
                result = inventory.grant(account, operation, job["plan"]["configurationId"])
            else:
                result = wallet.change(account, operation, job["plan"]["price"], refund=step == "refund")
            if self.lost == step:
                self.lost = None
                raise ApplicationError(503, "Acknowledgement lost")
            return result

        def charge(self, job):
            return self.invoke("charge", job)

        def grant(self, job):
            return self.invoke("grant", job)

        def refund(self, job):
            return self.invoke("refund", job)

    gateway = Gateway()
    return SimpleNamespace(database=database, uow=uow, catalog=catalog, wallet=wallet, inventory=inventory,
        inventory_db=inventory_storage[0], definitions=definitions, gateway=gateway,
        api=ShopUseCases(uow, catalog), worker=SettlePurchases(uow, gateway), account=uuid4())


def buy(world, operation=None):
    operation = operation or uuid4()
    result = world.api.purchase(world.account, operation, "sword", world.catalog.version)
    return operation, result


def ready(world, operation):
    with world.database.transaction() as session:
        session.get(PurchaseJob, (world.account, operation)).available_at = datetime.now(timezone.utc) - timedelta(seconds=1)


def drain(world):
    for _ in range(10):
        if not world.worker.once():
            return
    pytest.fail("Worker did not settle")


def weapon_count(world):
    with world.inventory_db.transaction() as session:
        return session.scalar(select(func.count()).select_from(OwnedWeapon).where(
            OwnedWeapon.account_id == world.account, OwnedWeapon.configuration_id == "sword.rare"))


@pytest.mark.parametrize("price", [0, 25])
def test_purchase_completes_with_server_price_and_history(world, price):
    world.catalog.price = price
    operation, result = buy(world)
    assert result["status"] == "Pending"
    # The accepted plan is independent of later deployment changes.
    world.catalog.price = 400
    world.catalog.unavailable = True
    drain(world)
    receipt = world.api.receipt(world.account, operation)
    assert receipt["status"] == "Completed"
    assert receipt["chargedAmount"] == price
    assert receipt["inventoryRevision"] == world.inventory.get(world.account)["Revision"]
    assert world.wallet.balance(world.account)["balance"] == 500 - price
    assert weapon_count(world) == 1
    assert "plan" not in receipt
    assert world.api.purchase(world.account, operation, "sword", "v1")["replayed"]
    assert world.api.history(world.account)["purchases"][0]["operationId"] == str(operation)
    if price == 0:
        assert world.gateway.calls == ["grant"]


def test_insufficient_funds_does_not_grant_and_releases_reservation(world):
    world.catalog.price, world.catalog.limit = 600, 1
    operation, _ = buy(world)
    drain(world)
    assert world.api.receipt(world.account, operation)["code"] == "InsufficientFunds"
    assert world.wallet.balance(world.account)["balance"] == 500
    assert weapon_count(world) == 0
    assert world.api.get_catalog(world.account)["offers"][0]["purchasedCount"] == 0
    assert world.gateway.calls == ["charge"]


@pytest.mark.parametrize("offer,version,code", [("sword", "old", "CatalogChanged"),
                                              ("unknown", "v1", "OfferUnavailable")])
def test_rejected_request_has_no_job_and_replays_without_catalog(world, offer, version, code):
    operation = uuid4()
    result = world.api.purchase(world.account, operation, offer, version)
    assert result["status"] == "Rejected" and result["code"] == code
    assert not world.worker.once()
    world.catalog.unavailable = True
    replay = world.api.purchase(world.account, operation, offer, version)
    assert replay["replayed"] and replay["code"] == code
    assert world.gateway.calls == []


def test_inventory_rejected_refund_never_marks_purchase_refunded(world):
    operation, _ = buy(world)
    world.definitions.ids = ["sword.common"]
    world.gateway.refund = lambda job: {"status": "REJECTED", "code": "ChargeNotFound"}
    drain(world)
    assert world.api.receipt(world.account, operation)["status"] == "Review"
    assert world.wallet.balance(world.account)["balance"] == 475
    assert weapon_count(world) == 0


def test_concurrent_replays_create_one_job_and_one_grant(world):
    operation = uuid4()
    with ThreadPoolExecutor(max_workers=6) as pool:
        replies = list(pool.map(lambda _: buy(world, operation)[1], range(6)))
    assert sum(not r["replayed"] for r in replies) == 1
    # Other worker threads may hold their own short claim transactions.
    # The single-worker tests above verify that the calling worker releases its connection.
    world.gateway.check_connections = False
    with ThreadPoolExecutor(max_workers=4) as pool:
        list(pool.map(lambda _: drain(world), range(4)))
    drain(world)
    assert world.wallet.balance(world.account)["balance"] == 475
    assert weapon_count(world) == 1
    assert world.api.receipt(world.account, operation)["status"] == "Completed"


def test_lost_grant_ack_is_replayed_without_current_definitions(world):
    operation, _ = buy(world)
    world.gateway.lost = "grant"
    drain(world)
    assert weapon_count(world) == 1
    world.definitions.unavailable = True
    ready(world, operation)
    drain(world)
    assert world.api.receipt(world.account, operation)["status"] == "Completed"
    assert world.wallet.balance(world.account)["balance"] == 475
    assert "refund" not in world.gateway.calls
    assert weapon_count(world) == 1


def test_lost_refund_ack_retries_once_and_inventory_rejection_is_terminal(world):
    operation, _ = buy(world)
    world.definitions.ids = ["sword.common"]
    world.gateway.lost = "refund"
    drain(world)
    assert world.wallet.balance(world.account)["balance"] == 500
    assert world.api.receipt(world.account, operation)["status"] == "Pending"
    world.definitions.ids.append("sword.rare")
    # A previously rejected source cannot grant later, even if the definition reappears.
    assert world.inventory.grant(world.account, operation, "sword.rare")["status"] == "REJECTED"
    ready(world, operation)
    drain(world)
    assert world.api.receipt(world.account, operation)["code"] == "GrantRejected"
    assert world.wallet.balance(world.account)["balance"] == 500
    assert weapon_count(world) == 0


@pytest.mark.parametrize("step,balance", [("charge", 500), ("grant", 475), ("refund", 475)])
def test_conflict_requires_review_and_reserves_limit(world, step, balance):
    world.catalog.limit = 1
    operation, _ = buy(world)
    if step == "refund":
        world.definitions.ids = ["sword.common"]
    world.gateway.conflict = step
    drain(world)
    receipt = world.api.receipt(world.account, operation)
    assert receipt["status"] == "Review"
    assert receipt["code"] == "ParticipantConflict"
    assert "_resumeState" not in receipt
    assert world.wallet.balance(world.account)["balance"] == balance
    assert world.api.get_catalog(world.account)["offers"][0]["available"] is False
    world.gateway.conflict = None
    assert world.worker.requeue(world.account, operation)
    drain(world)
    assert world.api.receipt(world.account, operation)["status"] == ("Rejected" if step == "refund" else "Completed")


def test_history_uses_stable_cursor_and_is_scoped_to_owner(world):
    operations = [buy(world)[0] for _ in range(5)]
    # Ties must be ordered by operation UUID, not by an unstable OFFSET.
    with world.database.transaction() as session:
        for row in session.scalars(select(PurchaseOperation)):
            row.created_at = datetime(2026, 11, 17, tzinfo=timezone.utc)
    first = world.api.history(world.account, 2)
    buy(world)  # A new purchase must not shift the remaining pages.
    second = world.api.history(world.account, 2, UUID(first["nextCursor"]))
    third = world.api.history(world.account, 2, UUID(second["nextCursor"]))
    ids = [p["operationId"] for page in [first, second, third] for p in page["purchases"]]
    assert ids == [str(i) for i in sorted(operations, reverse=True)]
    assert third["nextCursor"] is None
    assert world.api.history(uuid4())["purchases"] == []
    with pytest.raises(ApplicationError) as error:
        world.api.history(uuid4(), 2, operations[0])
    assert error.value.status_code == 404
    assert all("plan" not in p and "_resumeState" not in p for p in first["purchases"])


def test_expired_lease_cannot_overwrite_new_worker_and_attempts_reset_per_step(world):
    operation, _ = buy(world)
    with world.uow() as work:
        stale = work.repository.claim()
    with world.database.transaction() as session:
        session.get(PurchaseJob, (world.account, operation)).attempts = 19
    ready(world, operation)
    with world.uow() as work:
        current = work.repository.claim()
    with world.uow() as work:
        assert work.repository.transition(stale, "REJECTED", {"status": "Rejected"}) is False
    result = world.gateway.charge(current)
    assert result["status"] == "APPLIED"
    with world.uow() as work:
        assert work.repository.transition(current, "CHARGED") is True
    with world.database.transaction() as session:
        assert session.get(PurchaseJob, (world.account, operation)).attempts == 0
    drain(world)
    assert world.api.receipt(world.account, operation)["status"] == "Completed"


def test_legacy_grant_is_replayed_before_fetching_removed_definition(world):
    operation = uuid4()
    with world.inventory_db.transaction() as session:
        # Existing installations have owned_weapons without inventory.purchase receipts.
        repo = InventoryRepository(session)
        repo.lock_player(world.account)
        repo.ensure(world.account)
        instance, _ = repo.grant(world.account, "purchase:" + str(operation), "sword.rare")
    world.definitions.unavailable = True
    reply = world.inventory.grant(world.account, operation, "sword.rare")
    assert reply["instanceId"] == str(instance)
    assert reply["status"] == "APPLIED"
    from vatican_inventory.application.errors import ApplicationError as InventoryError
    with pytest.raises(InventoryError) as error:
        world.inventory.grant(world.account, operation, "spear.rare")
    assert error.value.status_code == 409


@pytest.mark.parametrize("price,expected", [(25, "Completed"), (600, "Rejected")])
def test_http_purchase_flow_uses_real_participant_contracts_and_dishka(world, shop_storage, wallet_storage,
                                                                   inventory_storage, price, expected):
    world.catalog.price = price
    outsider = uuid4()

    class Auth:
        def authenticate(self, token):
            return SimpleNamespace(account_id=world.account if token == "player" else outsider)

    class DefinitionsProvider(Provider):
        @provide(scope=Scope.APP, provides=InventoryDefinitions)
        def definitions(self):
            return world.definitions

    wallet = wallet_app(WalletSettings(wallet_storage[1], "s" * 32), auth_gateway=Auth())
    inventory = inventory_app(InventorySettings(inventory_storage[1], "s" * 32),
                              providers=(DefinitionsProvider(),), auth_gateway=Auth())
    with TestClient(wallet) as wallet_client, TestClient(inventory) as inventory_client:
        def handler(request):
            assert world.database.engine.pool.checkedout() == 0
            client = wallet_client if request.url.host == "wallet" else inventory_client
            response = client.post(request.url.path, content=request.content, headers=dict(request.headers))
            return httpx.Response(response.status_code, json=response.json())

        settings = Settings(shop_storage[1], "s" * 32, wallet_url="http://wallet", inventory_url="http://inventory")
        with httpx.Client(transport=httpx.MockTransport(handler)) as client:
            adapter = PurchaseGateway(client, settings)

            class ShopProvider(Provider):
                @provide(scope=Scope.APP, provides=ShopCatalog)
                def catalog(self):
                    return world.catalog

                @provide(scope=Scope.APP, provides=PurchaseGatewayPort)
                def gateway(self):
                    return adapter

            app = create_app(settings, providers=(ShopProvider(),), auth_gateway=Auth())
            with TestClient(app) as shop:
                headers = {"Authorization": "Bearer player"}
                operation = uuid4()
                body = {"operationId": str(operation), "offerId": "sword", "catalogVersion": "v1"}
                assert shop.post("/v1/purchases", json=body).status_code == 401
                invalid = shop.post("/v1/purchases", headers=headers, json={**body, "price": 0})
                assert invalid.status_code == 422
                assert set(invalid.json()) == {"code", "detail", "requestId", "errors"}
                assert shop.post("/v1/purchases", headers=headers, json=body).status_code == 202
                worker = app.state.dishka_container.get(SettlePurchases)
                while worker.once():
                    pass
                receipt = shop.get(f"/v1/purchases/{operation}", headers=headers)
                assert receipt.json()["status"] == expected
                if expected == "Completed":
                    assert receipt.json()["inventoryRevision"] >= 2
                history = shop.get("/v1/purchases?limit=1", headers=headers)
                assert history.status_code == 200
                assert history.json()["purchases"][0]["operationId"] == str(operation)
                assert shop.get(f"/v1/purchases/{operation}", headers={"Authorization": "Bearer outsider"}).status_code == 404
                assert shop.get("/v1/purchases?limit=101", headers=headers).status_code == 422
                changed = shop.post("/v1/purchases", headers=headers, json={**body, "catalogVersion": "changed"})
                assert changed.status_code == 409
                assert changed.json()["code"] == "ResourceConflict"
