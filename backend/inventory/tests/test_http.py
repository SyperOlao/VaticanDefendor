from uuid import uuid4

from dishka import Provider, Scope, provide
from fastapi.testclient import TestClient
from fastapi.routing import APIRoute
from starlette.routing import Match
import httpx
import pytest

from vatican_inventory.application.errors import ApplicationError
from vatican_inventory.application.ports import DefinitionsGateway
from vatican_inventory.bootstrap.settings import Settings
from vatican_inventory.integrations.http import AuthHttpGateway, DefinitionsHttpGateway, Principal
from vatican_inventory.presentation.app import create_app


@pytest.fixture
def client(inventory):
    class ContentProvider(Provider):
        @provide(scope=Scope.APP, provides=DefinitionsGateway)
        def definitions(self) -> DefinitionsGateway:
            return inventory.definitions

    class Auth:
        def authenticate(self, token):
            assert inventory.database.engine.pool.checkedout() == 0
            if token != "player-token":
                raise ApplicationError(401, "Credential is invalid or expired")
            return Principal(inventory.account, True)

    with TestClient(create_app(Settings(inventory.url, "s" * 32), providers=(ContentProvider(),), auth_gateway=Auth())) as result:
        original = result.request
        def documented_request(*args, **kwargs):
            response = original(*args, **kwargs)
            scope = {'type': 'http', 'path': response.request.url.path, 'method': response.request.method, 'root_path': ''}
            route = next((r for r in result.app.routes if isinstance(r, APIRoute) and r.matches(scope)[0] == Match.FULL), None)
            if route is not None:
                status = response.status_code
                documented = result.app.openapi()['paths'][route.path][response.request.method.lower()]['responses']
                assert str(status) in documented, (route.path, status)
                field = route.response_field if status == 200 and route.response_field is not None else route.response_fields.get(status)
                assert field is not None, (route.path, status, 'Missing response schema')
                _, errors = field.validate(response.json(), {}, loc=('response',))
                assert not errors, (route.path, status, errors)
            return response
        result.request = documented_request
        yield result


def test_player_inventory_loadout_and_internal_snapshot_contract(client, inventory):
    headers = {"Authorization": "Bearer player-token"}
    assert client.get("/v1/weapons").status_code == 401
    assert client.get("/v1/inventory", headers={"Authorization": "Bearer invalid"}).status_code == 401
    first = client.get("/v1/weapons", headers=headers).json()
    assert len(first["Weapons"]) == 1
    request = {"InstanceIds": first["Loadout"], "expectedRevision": first["Revision"]}
    assert client.put("/v1/weapons/loadout", headers=headers, json=request).json() == first
    conflict = client.put("/v1/weapons/loadout", headers=headers, json={**request, "expectedRevision": 999})
    assert conflict.status_code == 409 and conflict.json()["code"] == "ResourceConflict"
    invalid = client.put("/v1/weapons/loadout", headers=headers, json={**request, "expectedRevision": True})
    assert invalid.status_code == 422 and invalid.json()["errors"][0]["field"] == "body.expectedRevision"
    assert client.put("/v1/weapons/loadout", headers=headers, json={**request, "accountId": str(uuid4())}).status_code == 422
    assert client.get("/v1/inventory", headers=headers).json() == {"items": [], "revision": 1}
    path = f"/internal/players/{inventory.account}/snapshot"
    assert client.get(path, headers=headers).status_code == 401
    assert client.get(path, headers={"X-Service-Secret": "s" * 32}).json() == first
    assert client.get("/health/ready").status_code == 200
    assert client.get("/openapi.json").json()["components"]["schemas"]["Loadout"]["properties"]["expectedRevision"]


def test_message_auth_validation_and_saved_run_receipt(client, inventory):
    headers = {"X-Service-Secret": "s" * 32}
    message = {"messageId": str(uuid4()), "eventName": "run.weapon_reward", "schemaVersion": 1,
               "payload": {"accountId": str(inventory.account), "runId": str(uuid4()), "configurationId": "axe.rare",
                           "snapshot": inventory.api.get(inventory.account, selected_only=True)}}
    assert client.post("/internal/messages", json=message).status_code == 401
    assert client.post("/internal/messages", headers={"X-Service-Secret": "wrong"}, json=message).status_code == 401
    first = client.post("/internal/messages", headers=headers, json=message)
    assert first.status_code == 200 and first.json()["receipt"]["runId"] == message["payload"]["runId"]
    inventory.definitions.down = True
    assert client.post("/internal/messages", headers=headers, json=message).json() == first.json()
    invalid = client.post("/internal/messages", headers=headers, json={**message, "schemaVersion": True})
    assert invalid.status_code == 422
    invalid = client.post("/internal/messages", headers=headers, json={**message, "payload": {**message["payload"], "snapshot": {}}})
    assert invalid.status_code == 422 and set(invalid.json()) == {"code", "detail", "requestId", "errors"}


def test_purchase_grants_are_internal_and_receipts_survive_content_outage(client, inventory):
    headers = {"X-Service-Secret": "s" * 32}
    request = {"accountId": str(inventory.account), "operationId": str(uuid4()), "configurationId": "axe.rare"}
    assert client.post("/internal/grants", json=request).status_code == 401
    first = client.post("/internal/grants", headers=headers, json=request)
    assert first.status_code == 200 and first.json()["status"] == "APPLIED"
    inventory.definitions.down = True
    assert client.post("/internal/grants", headers=headers, json=request).json() == first.json()
    assert client.post("/internal/grants", headers=headers, json={**request, "configurationId": "sword.common"}).status_code == 409


def test_keyed_loadout_query_and_single_weapon_are_owner_scoped(client, inventory):
    headers = {"Authorization": "Bearer player-token"}
    first = client.get("/v1/weapons", headers=headers).json()
    starter = first["Weapons"][0]["InstanceId"]
    detail = client.get("/v1/weapons/" + starter, headers=headers)
    assert detail.status_code == 200 and detail.json()["Weapon"] == first["Weapons"][0]
    foreign = inventory.api.get(uuid4())["Weapons"][0]["InstanceId"]
    assert client.get("/v1/weapons/" + foreign, headers=headers).status_code == 404
    key = str(uuid4())
    body = {"InstanceIds": [starter], "expectedRevision": first["Revision"], "requestKey": key}
    result = client.put("/v1/weapons/loadout", headers=headers, json=body)
    assert result.status_code == 200 and result.json()["loadoutOperation"]["requestKey"] == key
    inventory.definitions.down = True
    assert client.put("/v1/weapons/loadout", headers=headers, json=body).json() == result.json()
    path = "/v1/weapons/loadout/operations/" + key
    assert client.get(path).status_code == 401
    assert client.get(path, headers=headers).json() == result.json()["loadoutOperation"]
    assert client.get("/v1/weapons/loadout/operations/" + str(uuid4()), headers=headers).status_code == 404


def test_consumption_http_reconciles_message_delivery_and_does_not_expose_other_accounts(client, inventory):
    service = {"X-Service-Secret": "s" * 32}
    player = {"Authorization": "Bearer player-token"}
    item = "item:repair-kit"
    inventory.api.receive(uuid4(), "level.reward", 1, {"accountId": str(inventory.account), "claimId": str(uuid4()),
        "itemIndex": 0, "rewardType": "ITEM", "rewardRef": "repair-kit", "quantity": 4})
    request = {"accountId": str(inventory.account), "operationId": str(uuid4()), "itemId": item, "quantity": 3}
    path = "/internal/items/consumptions"
    assert client.post(path, headers=player, json=request).status_code == 401
    assert client.post(path, headers=service, json={**request, "quantity": True}).status_code == 422
    assert client.post(path, headers=service, json={**request, "itemId": "case:daily"}).status_code == 422
    result = client.post(path, headers=service, json=request)
    assert result.status_code == 200 and result.json()["quantityAfter"] == 1
    message = {"messageId": str(uuid4()), "eventName": "inventory.item.consume", "schemaVersion": 1, "payload": request}
    assert client.post("/internal/messages", headers=service, json=message).json()["receipt"] == result.json()
    receipt_path = f'/internal/players/{inventory.account}/item-consumptions/{request["operationId"]}'
    assert client.get(receipt_path, headers=player).status_code == 401
    assert client.get(receipt_path, headers=service).json() == result.json()
    assert client.get(receipt_path.replace(str(inventory.account), str(uuid4())), headers=service).status_code == 404
    assert client.get("/v1/inventory/" + item, headers=player).json()["quantity"] == 1
    assert client.get("/v1/inventory/item:missing", headers=player).status_code == 404
    assert client.post(path, headers=service, json={**request, "quantity": 1}).status_code == 409


def test_grant_lookup_keeps_revision_and_returns_no_result_for_another_owner(client, inventory):
    headers = {"X-Service-Secret": "s" * 32}
    request = {"accountId": str(inventory.account), "operationId": str(uuid4()), "configurationId": "axe.rare"}
    reply = client.post("/internal/grants", headers=headers, json=request).json()
    assert reply["inventoryRevision"] == inventory.api.get(inventory.account)["Revision"]
    inventory.definitions.down = True
    path = f'/internal/players/{inventory.account}/grants/{request["operationId"]}'
    assert client.get(path).status_code == 401
    assert client.get(path, headers=headers).json() == reply
    assert client.get(path.replace(str(inventory.account), str(uuid4())), headers=headers).status_code == 404


@pytest.mark.parametrize('mock', [False, True])
def test_openapi_matches_registered_routes_examples_and_authentication(mock):
    from vatican_inventory.application.catalog import EventName
    from vatican_inventory.presentation.examples import MOCK_GRANT
    settings = Settings('postgresql://unused@127.0.0.1/unused', 's'*32, app_environment='test',
                        mock_payments_enabled=mock, mock_grant_secret='m'*32)
    app = create_app(settings)
    with TestClient(app) as client:
        schema = client.get('/openapi.json').json()
        routes = [r for r in app.routes if isinstance(r, APIRoute)]
        assert {(r.path, m.lower()) for r in routes for m in r.methods} == {
            (path, method) for path, item in schema['paths'].items() for method in item}
        for route in routes:
            method = next(iter(route.methods)).lower()
            operation = schema['paths'][route.path][method]
            assert operation['summary'] and operation['description'] and operation['tags']
            if route.path.startswith('/v1/'):
                assert operation['security'] == [{'HTTPBearer': []}]
            elif route.path == '/internal/mock-payment-grants':
                assert operation['security'] == [{'ServiceSecret': [], 'MockGrantSecret': []}]
                # Having only one of the two keys must not authorize this method.
                for headers in ({'X-Service-Secret': 's'*32}, {'X-Mock-Grant-Key': 'm'*32}):
                    denied = client.post(route.path, headers=headers, json=MOCK_GRANT)
                    assert denied.status_code == 401 and denied.json()['code'] == 'InvalidCredentials'
            elif route.path.startswith('/internal/'):
                assert operation['security'] == [{'ServiceSecret': []}]
            else:assert not operation.get('security')
            for status, response in operation['responses'].items():
                content = response['content']['application/json']
                assert content.get('examples'), (route.path, status)
                assert content['schema'] != {}, (route.path, status)
                field = route.response_field if status == '200' and route.response_field is not None else route.response_fields[int(status)]
                for example in content['examples'].values():
                    _, errors = field.validate(example['value'], {}, loc=('example',))
                    assert not errors, (route.path, status, errors)
                if int(status) >= 400:
                    assert content['schema'] == {'$ref': '#/components/schemas/ApiError'}
            if route.body_field:
                for example in operation['requestBody']['content']['application/json']['examples'].values():
                    _, errors = route.body_field.validate(example['value'], {}, loc=('body',))
                    assert not errors, (route.path, errors)
        props = schema['components']['schemas']['Message']['properties']
        assert set(props['eventName']['enum']) == set(EventName) and props['schemaVersion']['enum'] == [1]
        assert ('/internal/mock-payment-grants' in schema['paths']) is mock


def test_broker_status_is_internal_and_reports_persisted_command_without_reapplying_it(client, inventory):
    import json
    from vatican_inventory.application.broker import AcceptBrokerDelivery, ProcessBrokerCommands
    identity = uuid4()
    envelope = {"messageId": str(identity), "eventName": "level.reward", "schemaVersion": 1,
        "payload": {"accountId": str(inventory.account), "claimId": str(uuid4()), "itemIndex": 0,
                    "rewardType": "ITEM", "rewardRef": "kit", "quantity": 2}}
    AcceptBrokerDelivery(inventory.api.uow).execute(json.dumps(envelope).encode())
    path = "/internal/broker/commands/" + str(identity)
    assert client.get(path).status_code == 401
    headers = {"X-Service-Secret": "s" * 32}
    assert client.get(path, headers=headers).json()["status"] == "PENDING"
    assert ProcessBrokerCommands(inventory.api.uow, inventory.api).once()
    result = client.get(path, headers=headers)
    assert result.status_code == 200 and result.json()["status"] == "APPLIED"
    assert result.json()["result"]["receipt"]["quantityAfter"] == 2
    assert result.json()["events"][0]["status"] == "PENDING"
    assert client.get("/internal/broker/commands/" + str(uuid4()), headers=headers).status_code == 404


@pytest.mark.parametrize("body", [None, {}, {"accountId": "invalid", "isGuest": True},
    {"accountId": str(uuid4()), "isGuest": 1}, {"accountId": 123, "isGuest": False}, {"accountId": [], "isGuest": True}])
def test_auth_gateway_fails_closed_on_malformed_principal(body):
    def respond(request):
        assert request.headers["Authorization"] == "Bearer secret-token"
        return httpx.Response(200, json=body)
    with httpx.Client(transport=httpx.MockTransport(respond)) as http:
        with pytest.raises(ApplicationError) as error:
            AuthHttpGateway(http, "http://auth").authenticate("secret-token")
    assert error.value.status_code == 503


@pytest.mark.parametrize("authorization", [None, "Basic credentials", "Bearer ", "Bearer one two", "Bearer токен",
    "Bearer \x00", "Bearer \x7f", "Bearer " + "a" * 513])
def test_invalid_bearer_is_rejected_before_auth_http(authorization):
    from vatican_inventory.application.access import AuthenticatePlayer
    class Auth:
        def authenticate(self, token):
            raise AssertionError("Invalid credentials must never reach the HTTP adapter")
    with pytest.raises(ApplicationError) as error:
        AuthenticatePlayer(Auth()).execute(authorization)
    assert error.value.status_code == 401


@pytest.mark.parametrize("body", [None, [], [{"Id": "sword.common"}],
    [{"Id": "sword.common", "MaximumLevel": 20, "ExperiencePerLevel": True}]])
def test_content_gateway_rejects_incomplete_or_invalid_definitions(body):
    with httpx.Client(transport=httpx.MockTransport(lambda request: httpx.Response(200, json=body))) as http:
        with pytest.raises(ApplicationError) as error:
            DefinitionsHttpGateway(http, "http://game").get()
    assert error.value.status_code == 503
