import json
from uuid import uuid4

import httpx
import pytest

from vatican_shop.application.errors import ApplicationError, ParticipantConflict
from vatican_shop.bootstrap.settings import Settings
from vatican_shop.domain.catalogs import build_catalog
from vatican_shop.integrations.shop import PurchaseGateway
from vatican_shop.cli.import_catalog import read_catalog


@pytest.fixture
def job():
    return {"accountId": uuid4(), "operationId": uuid4(), "plan": {"price": 25, "configurationId": "sword.rare"}}


def gateway(handler):
    return PurchaseGateway(httpx.Client(transport=httpx.MockTransport(handler)), Settings("unused", "s" * 32))


def acknowledgement(request, **changes):
    body = json.loads(request.content)
    return {**body, "status": "APPLIED", "currencyId": "PRESTIGE", "instanceId": str(uuid4()), **changes}


@pytest.mark.parametrize("method,code", [("charge", "InsufficientFunds"), ("refund", "ChargeNotFound"),
                                        ("grant", "UnknownWeaponConfiguration")])
@pytest.mark.parametrize("status", ["APPLIED", "REJECTED"])
def test_gateway_accepts_only_scoped_command_acknowledgements(job, method, code, status):
    def handler(request):
        assert request.headers["X-Service-Secret"] == "s" * 32
        return httpx.Response(200, json=acknowledgement(request, status=status, code=code))
    result = getattr(gateway(handler), method)(job)
    assert result["status"] == status


@pytest.mark.parametrize("changes", [{"operationId": str(uuid4())}, {"accountId": str(uuid4())},
    {"amount": 26}, {"amount": True}, {"currencyId": "GEMS"}, {"status": "UNKNOWN"},
    {"status": "REJECTED", "code": "SomeOtherConflict"}])
def test_wallet_acknowledgement_must_match_owner_amount_currency_and_source(job, changes):
    adapter = gateway(lambda request: httpx.Response(200, json=acknowledgement(request, **changes)))
    with pytest.raises(ApplicationError) as error:
        adapter.charge(job)
    assert error.value.status_code == 503


@pytest.mark.parametrize("changes", [{"configurationId": "spear.rare"}, {"instanceId": "invalid"},
    {"instanceId": None}, {"instanceId": "00000000-0000-0000-0000-000000000000"}])
def test_inventory_acknowledgement_must_match_weapon_and_valid_instance(job, changes):
    adapter = gateway(lambda request: httpx.Response(200, json=acknowledgement(request, **changes)))
    with pytest.raises(ApplicationError):
        adapter.grant(job)


@pytest.mark.parametrize("body", [None, [], "APPLIED", {"status": "APPLIED"}])
def test_malformed_acknowledgement_is_retryable(job, body):
    with pytest.raises(ApplicationError) as error:
        gateway(lambda request: httpx.Response(200, json=body)).charge(job)
    assert error.value.status_code == 503


@pytest.mark.parametrize("status", [409, 422])
def test_generic_participant_error_requires_reconciliation(job, status):
    with pytest.raises(ParticipantConflict):
        gateway(lambda request: httpx.Response(status, json={"code": "ResourceConflict"})).charge(job)


@pytest.mark.parametrize("status", [202, 301, 401, 403, 404, 429, 500, 503])
def test_http_failure_never_confirms_a_command(job, status):
    with pytest.raises(ApplicationError) as error:
        gateway(lambda request: httpx.Response(status)).grant(job)
    assert error.value.status_code == 503


def test_connection_loss_is_retryable(job):
    def handler(request):
        raise httpx.ReadTimeout("private transport diagnostics", request=request)
    with pytest.raises(ApplicationError) as error:
        gateway(handler).refund(job)
    assert error.value.status_code == 503
    assert "private" not in error.value.detail


def configured_catalog():
    return {"version": "v1", "offers": [{"id": "sword", "weaponConfigurationId": "sword.rare",
            "title": "Rare sword", "price": 25, "purchaseLimit": 0}]}


def test_catalog_version_tracks_price_and_weapon_definition_without_mutating_input():
    configured = configured_catalog()
    definitions = [{"Id": "sword.rare", "Damage": 10}]
    first = build_catalog(configured, definitions)
    assert "currencyId" not in configured["offers"][0]
    definitions[0]["Damage"] = 11
    assert build_catalog(configured, definitions)["version"] != first["version"]
    definitions[0]["Damage"] = 10
    configured["offers"][0]["price"] = 26
    assert build_catalog(configured, definitions)["version"] != first["version"]


@pytest.mark.parametrize("contents", ["{broken", "[]", '{"version":"v1","offers":[{}]}'])
def test_bad_import_file_returns_safe_error(tmp_path, contents):
    path = tmp_path / "catalog.json"
    path.write_text(contents)
    with pytest.raises(ApplicationError) as error:
        read_catalog(path)
    assert error.value.status_code == 422
    assert str(path) not in error.value.detail


def test_missing_import_file_returns_safe_error(tmp_path):
    with pytest.raises(ApplicationError) as error:
        read_catalog(tmp_path / "missing")
    assert error.value.status_code == 422


def test_empty_import_file_is_valid(tmp_path):
    path = tmp_path / "empty.json"
    path.write_text('{"version":"empty","offers":[]}')
    assert read_catalog(path)["offers"] == []
