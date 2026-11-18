from concurrent.futures import ThreadPoolExecutor
from types import SimpleNamespace
from uuid import uuid4

import httpx
import pytest
from dishka import Provider, Scope, provide
from fastapi.testclient import TestClient
from sqlalchemy import func, select

from vatican_shop.application.content import AdminCatalog, ReadCatalog
from vatican_shop.application.errors import ApplicationError
from vatican_shop.application.models import Principal
from vatican_shop.application.ports import WeaponDefinitionsGateway
from vatican_shop.application.use_cases import ShopUseCases
from vatican_shop.bootstrap.settings import Settings
from vatican_shop.integrations.http import AuthHttpGateway
from vatican_shop.persistence.postgres.repositories.catalog import CatalogRepository
from vatican_shop.persistence.postgres.tables import ShopCatalogAudit, ShopCatalogState, ShopOffer
from vatican_shop.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory
from vatican_shop.presentation.app import create_app


def offer(offer_id="sword", **changes):
    return {"id": offer_id, "weaponConfigurationId": "sword.rare", "title": "Rare sword", "price": 25,
            "purchaseLimit": 0, "available": True, "unavailableReason": "", **changes}


@pytest.fixture
def catalog_world(shop_storage):
    database, url = shop_storage
    uow = PostgresUnitOfWorkFactory(database)

    class Auth:
        admin = Principal(uuid4(), False, ("ADMIN",))
        calls = 0
        revoked = False

        def authenticate(self, token):
            assert database.engine.pool.checkedout() == 0
            self.calls += 1
            if token == "admin" and not self.revoked:
                return self.admin
            if token == "guest-admin":
                return Principal(uuid4(), True, ("ADMIN",))
            if token in ("player", "admin"):
                return Principal(uuid4(), False)
            if token == "outage":
                raise ApplicationError(503, "Auth unavailable")
            raise ApplicationError(401, "Invalid credential")

    class Definitions:
        calls = 0
        unavailable = False

        def get(self):
            assert database.engine.pool.checkedout() == 0
            self.calls += 1
            if self.unavailable:
                raise ApplicationError(503, "Definitions unavailable")
            return [{"Id": "sword.rare"}, {"Id": "spear.rare"}]

    auth, definitions = Auth(), Definitions()
    reader = ReadCatalog(uow, definitions)
    return SimpleNamespace(database=database, url=url, uow=uow, auth=auth, definitions=definitions,
        admin=AdminCatalog(uow, auth, definitions), reader=reader, shop=ShopUseCases(uow, reader))


def test_create_edit_delete_updates_live_catalog_and_audit(catalog_world):
    w = catalog_world
    empty = w.reader.get()
    assert empty["offers"] == [] and w.definitions.calls == 0
    created = w.admin.create("admin", offer())
    first = w.reader.get()
    assert first["offers"][0]["price"] == 25
    assert first["version"] != empty["version"]
    updated = w.admin.update("admin", "sword", created["revision"], offer(price=30))
    assert updated["revision"] == 2
    second = w.reader.get()
    assert second["version"] != first["version"]
    assert second["offers"][0]["price"] == 30
    w.admin.delete("admin", "sword", updated["revision"])
    assert w.reader.get()["offers"] == []
    assert w.admin.list("admin")["offers"] == []
    assert w.admin.list("admin", include_deleted=True)["offers"][0]["deletedAt"] is not None
    with w.database.transaction() as session:
        audit = list(session.scalars(select(ShopCatalogAudit).order_by(ShopCatalogAudit.created_at)))
        assert [row.action for row in audit] == ["shop.offer.create", "shop.offer.update", "shop.offer.delete"]
        assert all(row.actor_account_id == w.auth.admin.account_id for row in audit)
        assert audit[0].before_json is None
        assert audit[1].before_json["price"] == 25 and audit[1].after_json["price"] == 30
        assert audit[2].after_json["deletedAt"] is not None


@pytest.mark.parametrize("token,status", [("player", 403), ("guest-admin", 403), ("invalid", 401), ("outage", 503)])
@pytest.mark.parametrize("method", ["list", "get", "create", "update", "delete", "import"])
def test_every_use_case_checks_current_auth_before_database(catalog_world, token, status, method):
    w = catalog_world
    actions = {
        "list": lambda: w.admin.list(token), "get": lambda: w.admin.get(token, "sword"),
        "create": lambda: w.admin.create(token, offer()),
        "update": lambda: w.admin.update(token, "sword", 1, offer(price=30)),
        "delete": lambda: w.admin.delete(token, "sword", 1),
        "import": lambda: w.admin.import_catalog(token, {"version": "v1", "offers": [offer()]}),
    }
    with pytest.raises(ApplicationError) as error:
        actions[method]()
    assert error.value.status_code == status
    assert w.definitions.calls == 0
    with w.database.transaction() as session:
        assert session.scalar(select(func.count()).select_from(ShopOffer)) == 0


@pytest.mark.parametrize("change", [{"price": -1}, {"price": True}, {"title": "   "},
    {"title": " " + "x" * 80 + " "}, {"available": "true"}, {"purchaseLimit": -1}, {"currencyId": "GEMS"},
    {"available": True, "unavailableReason": "Disabled"}, {"weaponConfigurationId": "missing"}])
def test_invalid_offer_never_changes_catalog(catalog_world, change):
    w = catalog_world
    with pytest.raises(ApplicationError) as error:
        w.admin.create("admin", offer(**change))
    assert error.value.status_code == 422
    assert w.reader.get()["offers"] == []


def test_revoking_role_takes_effect_on_the_next_request(catalog_world):
    w = catalog_world
    w.admin.create("admin", offer())
    w.auth.revoked = True
    with pytest.raises(ApplicationError) as error:
        w.admin.delete("admin", "sword", 1)
    assert error.value.status_code == 403
    assert len(w.reader.get()["offers"]) == 1


def test_catalog_edit_keeps_pending_purchase_plan_and_rejects_old_version(catalog_world):
    w = catalog_world
    w.admin.create("admin", offer())
    account, operation = uuid4(), uuid4()
    original = w.reader.get()
    w.shop.purchase(account, operation, "sword", original["version"])
    w.admin.update("admin", "sword", 1, offer(price=100, weaponConfigurationId="spear.rare"))
    rejected = w.shop.purchase(account, uuid4(), "sword", original["version"])
    assert rejected["code"] == "CatalogChanged"
    w.admin.delete("admin", "sword", 2)
    replay = w.shop.purchase(account, operation, "sword", original["version"])
    assert replay["status"] == "Pending" and replay["replayed"]
    with w.uow() as work:
        job = work.repository.claim()
    assert job["plan"] == {"price": 25, "configurationId": "sword.rare"}
    with pytest.raises(ApplicationError) as error:
        w.admin.create("admin", offer())
    assert error.value.status_code == 409  # Deleted IDs cannot reset lifetime purchase limits.


def test_edit_between_catalog_fetch_and_acceptance_is_detected(catalog_world):
    w = catalog_world
    w.admin.create("admin", offer())
    stale = w.reader.get()

    class RacingCatalog:
        def get(self):
            w.admin.delete("admin", "sword", 1)
            return stale

    shop = ShopUseCases(w.uow, RacingCatalog())
    reply = shop.purchase(uuid4(), uuid4(), "sword", stale["version"])
    assert reply["status"] == "Rejected" and reply["code"] == "CatalogChanged"


def test_concurrent_edits_use_expected_revision(catalog_world):
    w = catalog_world
    w.admin.create("admin", offer())
    # No content lookup is needed when the reference is unchanged.
    def edit(price):
        try:
            return w.admin.update("admin", "sword", 1, offer(price=price))["revision"]
        except ApplicationError as error:
            return error.status_code
    # Pool-wide connection assertions apply to single-call tests, not other threads' transactions.
    w.auth.authenticate = lambda token: w.auth.admin
    with ThreadPoolExecutor(max_workers=2) as pool:
        results = list(pool.map(edit, [30, 40]))
    assert sorted(results) == [2, 409]
    assert w.admin.get("admin", "sword")["revision"] == 2


def test_price_update_and_delete_work_during_content_outage(catalog_world):
    w = catalog_world
    w.admin.create("admin", offer())
    w.definitions.unavailable = True
    w.admin.update("admin", "sword", 1, offer(price=30))
    w.admin.delete("admin", "sword", 2)
    assert w.reader.get()["offers"] == []


def test_audit_failure_rolls_back_offer_and_catalog_version(catalog_world, monkeypatch):
    w = catalog_world
    original = CatalogRepository._audit
    def fail(self, *args):
        original(self, *args)
        self.session.flush()
        raise RuntimeError("simulated failure")
    monkeypatch.setattr(CatalogRepository, "_audit", fail)
    with pytest.raises(RuntimeError):
        w.admin.create("admin", offer())
    assert w.reader.get()["offers"] == []
    with w.database.transaction() as session:
        assert session.get(ShopCatalogState, 1).revision == 1
        assert session.scalar(select(func.count()).select_from(ShopCatalogAudit)) == 0


def test_legacy_import_is_atomic_idempotent_and_does_not_replace_admin_edits(catalog_world):
    w = catalog_world
    data = {"version": "old", "offers": [offer(), offer("spear")]}
    assert w.admin.import_catalog("admin", data) == {"created": 2, "unchanged": 0}
    assert w.admin.import_catalog("admin", data) == {"created": 0, "unchanged": 2}
    w.admin.update("admin", "sword", 1, offer(price=200))
    data["offers"].insert(0, offer("axe"))
    with pytest.raises(ApplicationError):
        w.admin.import_catalog("admin", data)
    assert {o["id"] for o in w.reader.get()["offers"]} == {"sword", "spear"}


def test_noop_edit_does_not_increment_revisions_or_audit(catalog_world):
    w = catalog_world
    w.admin.create("admin", offer())
    before = w.reader.get()
    result = w.admin.update("admin", "sword", 1, offer())
    assert result["revision"] == 1 and w.reader.get()["version"] == before["version"]
    with w.database.transaction() as session:
        assert session.scalar(select(func.count()).select_from(ShopCatalogAudit)) == 1


def test_catalog_limit_counts_disabled_offers_and_deleted_id_cannot_be_reused(catalog_world):
    w = catalog_world
    data = {"version": "full", "offers": [offer("offer_" + str(i), available=False) for i in range(200)]}
    assert w.admin.import_catalog("admin", data)["created"] == 200
    with pytest.raises(ApplicationError) as error:
        w.admin.create("admin", offer("extra"))
    assert error.value.status_code == 409
    w.admin.delete("admin", "offer_0", 1)
    w.admin.create("admin", offer("extra"))
    with pytest.raises(ApplicationError) as error:
        w.admin.create("admin", offer("offer_0"))
    assert error.value.status_code == 409


def test_admin_http_crud_pagination_permissions_and_validation(catalog_world):
    w = catalog_world
    class DefinitionsProvider(Provider):
        @provide(scope=Scope.APP, provides=WeaponDefinitionsGateway)
        def definitions(self):
            return w.definitions
    app = create_app(Settings(w.url, "s" * 32), providers=(DefinitionsProvider(),), auth_gateway=w.auth)
    path, headers = "/v1/admin/shop/offers", {"Authorization": "Bearer admin"}
    with TestClient(app) as client:
        assert client.post(path, json=offer()).status_code == 401
        assert client.post(path, json=offer(), headers={"Authorization": "Bearer player"}).status_code == 403
        assert client.post(path, json=offer(), headers={"X-Service-Secret": "s" * 32}).status_code == 401
        assert client.post(path, json=offer(), headers=headers).status_code == 201
        assert client.post(path, json=offer("spear"), headers=headers).status_code == 201
        first = client.get(path + "?limit=1", headers=headers).json()
        second = client.get(path, params={"limit": 1, "after": first["nextCursor"]}, headers=headers).json()
        assert len(first["offers"]) == len(second["offers"]) == 1 and second["nextCursor"] is None
        invalid = client.put(path + "/sword", json={**offer(), "expectedRevision": 1, "role": "ADMIN"}, headers=headers)
        assert invalid.status_code == 422
        body = {k: v for k, v in offer(price=50).items() if k != "id"}
        response = client.put(path + "/sword", json={**body, "expectedRevision": 1}, headers=headers)
        assert response.status_code == 200 and response.json()["revision"] == 2
        assert client.put(path + "/sword", json={**body, "expectedRevision": 1}, headers=headers).status_code == 409
        assert client.delete(path + "/sword", headers=headers).status_code == 422
        assert client.delete(path + "/sword?expectedRevision=2", headers=headers).status_code == 204
        assert client.get(path + "/sword", headers=headers).json()["deletedAt"] is not None
        assert client.get("/v1/shop", headers=headers).json()["offers"][0]["id"] == "spear"
        w.auth.revoked = True
        denied = client.delete(path + "/spear?expectedRevision=1", headers=headers)
        assert denied.status_code == 403
        assert set(denied.json()) == {"code", "detail", "requestId", "errors"}


@pytest.mark.parametrize("roles,is_guest,status", [(["ADMIN"], False, 200), ([], False, 403),
    (["ADMIN"], True, 403), ("ADMIN", False, 503), (None, False, 503)])
def test_auth_http_contract_roles_are_not_taken_from_client(catalog_world, roles, is_guest, status):
    w = catalog_world
    def handler(request):
        assert request.url.path == "/v1/auth/me"
        assert request.headers["Authorization"] == "Bearer opaque-token"
        return httpx.Response(200, json={"accountId": str(uuid4()), "isGuest": is_guest, "roles": roles})
    with httpx.Client(transport=httpx.MockTransport(handler)) as client:
        admin = AdminCatalog(w.uow, AuthHttpGateway(client, "http://auth"), w.definitions)
        if status == 200:
            assert admin.list("opaque-token")["offers"] == []
        else:
            with pytest.raises(ApplicationError) as error:
                admin.list("opaque-token")
            assert error.value.status_code == status


@pytest.mark.parametrize("account", [None, 12, {}, [], "invalid"])
def test_malformed_auth_account_never_authorizes(catalog_world, account):
    w = catalog_world
    with httpx.Client(transport=httpx.MockTransport(lambda request: httpx.Response(200, json={
        "accountId": account, "isGuest": False, "roles": ["ADMIN"]}))) as client:
        admin = AdminCatalog(w.uow, AuthHttpGateway(client, "http://auth"), w.definitions)
        with pytest.raises(ApplicationError) as error:
            admin.list("token")
        assert error.value.status_code == 503
