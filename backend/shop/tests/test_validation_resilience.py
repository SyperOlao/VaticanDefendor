"""Malformed requests must fail before storage or participant commands."""
import json
from uuid import UUID, uuid4

from fastapi.testclient import TestClient
import httpx
import pytest
from sqlalchemy import func, select

from vatican_shop.application.content import AdminCatalog
from vatican_shop.application.errors import ApplicationError
from vatican_shop.application.models import Principal
from vatican_shop.application.operations import AdminPurchases
from vatican_shop.application.use_cases import ShopUseCases
from vatican_shop.domain.catalogs import validate_catalog
from vatican_shop.integrations.http import AuthHttpGateway
from vatican_shop.persistence.postgres.tables import PurchaseOperation
from .test_gateways import configured_catalog
from .test_resilience import app_for, w, world


NIL = "00000000-0000-0000-0000-000000000000"


class NoDependency:
    def __call__(self):
        pytest.fail("Invalid input reached a database transaction")
    def get(self):
        pytest.fail("Invalid input reached the content service")


class Auth:
    def authenticate(self, token):
        return Principal(uuid4(), False, ("ADMIN",))


@pytest.mark.parametrize("field,value", [("operationId", NIL), ("catalogVersion", "bad\x00version"),
    ("catalogVersion", "bad/version"), ("catalogVersion", "\ud800")])
def test_bad_purchase_input_returns_422_without_persisting_a_job(w, field, value):
    body = {"operationId": str(uuid4()), "offerId": "sword", "catalogVersion": "v1", field: value}
    with TestClient(app_for(w)) as client:
        response = client.post("/v1/purchases", content=json.dumps(body).encode(),
            headers={"Authorization": "Bearer player", "Content-Type": "application/json"})
    assert response.status_code == 422
    with w.database.transaction() as session:
        assert session.scalar(select(func.count()).select_from(PurchaseOperation)) == 0
    assert w.gateway.calls == []


@pytest.mark.parametrize("account,operation,offer,version", [
    (UUID(NIL), uuid4(), "sword", "v1"), (uuid4(), UUID(NIL), "sword", "v1"),
    (uuid4(), uuid4(), "sword\x00", "v1"), (uuid4(), uuid4(), "sword", "bad\x00version")])
def test_purchase_use_case_rejects_invalid_source_before_dependencies(account, operation, offer, version):
    with pytest.raises(ApplicationError) as error:
        ShopUseCases(NoDependency(), NoDependency()).purchase(account, operation, offer, version)
    assert error.value.status_code == 422


@pytest.mark.parametrize("title", ["Sword\x00", "Sword\ud800"])
def test_invalid_postgres_text_cannot_enter_catalog(title):
    configured = configured_catalog()
    configured["offers"][0]["title"] = title
    with pytest.raises(ValueError):
        validate_catalog(configured)


def test_nil_auth_identity_never_authorizes_an_administrator():
    with httpx.Client(transport=httpx.MockTransport(lambda request: httpx.Response(200, json={
        "accountId": NIL, "isGuest": False, "roles": ["ADMIN"]}))) as client:
        with pytest.raises(ApplicationError) as error:
            AuthHttpGateway(client, "http://auth").authenticate("token")
    assert error.value.status_code == 503


@pytest.mark.parametrize("method", ["get", "list", "delete"])
@pytest.mark.parametrize("identifier", ["bad\x00id", "\ud800"])
def test_admin_offer_keys_are_validated_before_a_database_transaction(method, identifier):
    admin = AdminCatalog(NoDependency(), Auth(), NoDependency())
    calls = {"get": lambda: admin.get("admin", identifier), "list": lambda: admin.list("admin", after=identifier),
             "delete": lambda: admin.delete("admin", identifier, 1)}
    with pytest.raises(ApplicationError) as error:
        calls[method]()
    assert error.value.status_code == 422


def test_invalid_unicode_reconciliation_reason_returns_validation_error():
    with pytest.raises(ApplicationError) as error:
        AdminPurchases(NoDependency(), Auth()).retry("admin", uuid4(), uuid4(), 1, uuid4(), "Checked\ud800")
    assert error.value.status_code == 422
