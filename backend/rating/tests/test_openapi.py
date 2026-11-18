"""Keep Swagger examples executable and aligned with the real HTTP contract."""
from copy import deepcopy
import json
from uuid import UUID, uuid4

from fastapi.testclient import TestClient
from fastapi.routing import APIRoute
import pytest
from sqlalchemy import update, func, text

from vatican_rating.application.broker import AcceptBrokerDelivery, ProcessBrokerCommands, PublishBrokerEvents
from vatican_rating.application.errors import ApplicationError
from vatican_rating.application.models import Principal
from vatican_rating.application.use_cases import RatingUseCases
from vatican_rating.bootstrap.settings import Settings
from vatican_rating.persistence.postgres.tables import BrokerInbox
from vatican_rating.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory
from vatican_rating.presentation.app import create_app
from vatican_rating.presentation import schemas
from vatican_rating.presentation.openapi import ACCOUNT_ID, REQUEST_ID
from vatican_rating.migrate import SCHEMA_REVISION

SECRET = "documentation-tests-only-" + "s" * 32
SERVICE_HEADERS = {"X-Service-Secret": SECRET, "X-Request-ID": REQUEST_ID}


@pytest.fixture
def docs_client():
    # Generating the specification and rendering docs must not need a live database/auth.
    with TestClient(create_app(Settings("postgresql://unused", SECRET))) as client:
        yield client


def operation(spec, path, method="get"):
    return spec["paths"][path][method]


def examples(op, status=200):
    return op["responses"][str(status)]["content"]["application/json"]["examples"]


def request_example(spec, name="run80"):
    return deepcopy(operation(spec, "/internal/messages", "post")["requestBody"]
                    ["content"]["application/json"]["examples"][name]["value"])


def test_docs_render_without_dependencies_and_expose_separate_credentials(docs_client):
    spec = docs_client.get("/openapi.json").json()
    assert docs_client.get("/docs").status_code == 200
    assert docs_client.get("/redoc").status_code == 200
    assert '"filter": true' in docs_client.get("/docs").text
    assert SECRET not in json.dumps(spec)
    assert "postgresql://unused" not in json.dumps(spec)
    actual_operations = {(route.path_format, method.lower()) for route in docs_client.app.routes
                         if isinstance(route, APIRoute) and route.include_in_schema for method in route.methods}
    assert {(path, method) for path, methods in spec['paths'].items() for method in methods} == actual_operations
    security = spec["components"]["securitySchemes"]
    assert security["HTTPBearer"]["scheme"] == "bearer"
    assert security["ServiceSecret"]["type"] == "apiKey"
    assert security["ServiceSecret"]["name"] == "X-Service-Secret"
    assert security["ServiceSecret"]["in"] == "header"
    assert operation(spec, "/v1/leaderboards/{code}/me")["security"] == [{"HTTPBearer": []}]
    for path, methods in spec["paths"].items():
        for op in methods.values():
            assert op["summary"] and op["description"] and op["tags"]
            if path.startswith("/internal/"):
                assert op["security"] == [{"ServiceSecret": []}]
                assert not any(p["in"] == "header" for p in op.get("parameters", []))
            elif not path.endswith("/me"):
                assert not op.get("security")
            for status, response in op["responses"].items():
                assert response["headers"]["X-Request-ID"]["schema"]["format"] == "uuid"
                if int(status) >= 400:
                    assert response["content"]["application/json"]["schema"] == {
                        "$ref": "#/components/schemas/ErrorResponse"}
                if status == "503":
                    assert response["headers"]["Retry-After"]["example"] == 1
    assert "HTTPValidationError" not in spec["components"]["schemas"]


def test_schema_describes_payload_and_all_examples_match_their_models(docs_client):
    spec = docs_client.get("/openapi.json").json()
    message = spec["components"]["schemas"]["Message"]
    payload = message["properties"]["payload"]
    assert set(payload["required"]) == {"accountId", "runId", "boardCode", "rulesetId", "contentReleaseId", "points"}
    assert payload["additionalProperties"] is False
    assert payload["properties"]["points"]["type"] == "integer"
    assert payload["properties"]["points"]["minimum"] == 0
    assert payload["properties"]["points"]["maximum"] == 9_223_372_036_854_775_807
    assert type(payload["properties"]["points"]["maximum"]) is int
    assert spec['components']['schemas']['RankedEntry']['properties']['score']['maximum'] == 9_223_372_036_854_775_807
    for path in ('/v1/leaderboards/{code}', '/v1/leaderboards/{code}/me'):
        parameter = next(p for p in operation(spec, path)['parameters'] if p['name'] == 'code')
        assert parameter['schema']['minLength'] == 1 and parameter['schema']['maxLength'] == 64
        assert parameter['schema']['pattern'] == r'^[A-Za-z0-9_.-]{1,64}$'
    assert message["properties"]["eventName"]["const"] == "run.rating"
    assert message["properties"]["schemaVersion"]["const"] == 1

    def resolve_refs(node):
        if isinstance(node, dict):
            if "$ref" in node:
                target = spec
                for part in node["$ref"].removeprefix("#/").split("/"):
                    target = target[part]
            for value in node.values():
                resolve_refs(value)
        elif isinstance(node, list):
            for value in node:
                resolve_refs(value)

    resolve_refs(spec)
    for methods in spec["paths"].values():
        for op in methods.values():
            for response in op["responses"].values():
                content = response["content"]["application/json"]
                model = getattr(schemas, content["schema"]["$ref"].rsplit("/", 1)[-1])
                for item in content["examples"].values():
                    assert model.model_validate(item["value"]).model_dump(mode="json") == item["value"]
    for name in ("run80", "runZero"):
        command = request_example(spec, name)
        assert schemas.Message.model_validate(command).model_dump(mode="json") == command
        schemas.RunRatingPayload.model_validate(command["payload"])


def test_documentation_does_not_coerce_immutable_payload(docs_client):
    command = request_example(docs_client.get("/openapi.json").json())
    raw_uuid = "{" + command["payload"]["accountId"].upper() + "}"
    command["payload"]["accountId"] = raw_uuid
    # The use case, not documentation models, must still reject floats and booleans.
    for value in (True, 1.5, 1e17):
        command["payload"]["points"] = value
        parsed = schemas.Message.model_validate(command)
        assert type(parsed.payload) is dict
        assert parsed.payload["accountId"] == raw_uuid
        assert type(parsed.payload["points"]) is type(value)
        assert parsed.payload["points"] == value


@pytest.mark.parametrize("path,method,kwargs,status,example_name", [
    ("/v1/leaderboards?limit=101", "get", {}, 422, "error"),
    ("/v1/leaderboards/run-xp/me", "get", {}, 401, "error"),
    ("/internal/messages/not-a-uuid", "get", {"headers": SERVICE_HEADERS}, 422, "invalidId"),
    ("/internal/messages", "post", {"content": "not-json"}, 401, "error"),
    ("/internal/messages", "post", {"headers": SERVICE_HEADERS, "content": "x" * 16385}, 413, "error"),
])
def test_documented_errors_match_actual_responses(docs_client, path, method, kwargs, status, example_name):
    kwargs = dict(kwargs)
    spec = docs_client.get("/openapi.json").json()
    if path.startswith("/v1/leaderboards?"):
        route = "/v1/leaderboards"
    elif path.startswith("/internal/messages/"):
        route = "/internal/messages/{message_id}"
    elif path.endswith("/me"):
        route = "/v1/leaderboards/{code}/me"
    else:
        route = path
    headers = {"X-Request-ID": REQUEST_ID, **kwargs.pop("headers", {})}
    reply = getattr(docs_client, method)(path, headers=headers, **kwargs)
    assert reply.status_code == status
    assert reply.json() == examples(operation(spec, route, method), status)[example_name]["value"]
    assert reply.headers["X-Request-ID"] == REQUEST_ID
    if status == 401:
        documented = operation(spec, route, method)['responses']['401']['headers']['WWW-Authenticate']['example']
        assert reply.headers['WWW-Authenticate'] == documented


def test_swagger_quickstart_and_repeated_requests_work_on_real_database(rating_storage):
    class Auth:
        def authenticate(self, token):
            assert token == "example-player-token"
            return Principal(UUID(ACCOUNT_ID), is_guest=True)

    with TestClient(create_app(Settings(rating_storage[1], SECRET), auth_gateway=Auth())) as client:
        spec = client.get("/openapi.json").json()
        for path, name in (('/health/live', 'live'), ('/health/ready', 'ready'), ('/v1/leaderboards', 'boards')):
            actual = client.get(path)
            assert actual.status_code == 200
            assert actual.json() == examples(operation(spec, path))[name]['value']
        assert client.get('/health/ready').json()['schemaRevision'] == SCHEMA_REVISION
        write_op = operation(spec, "/internal/messages", "post")
        ranking_op = operation(spec, "/v1/leaderboards/{code}")
        me_op = operation(spec, "/v1/leaderboards/{code}/me")
        player_headers = {"Authorization": "Bearer example-player-token"}
        assert client.get("/v1/leaderboards/run-xp").json() == examples(ranking_op)["empty"]["value"]
        assert client.get("/v1/leaderboards/run-xp/me", headers=player_headers).json() == examples(me_op)["unranked"]["value"]
        for request_name, reply_name in (("run80", "applied"), ("runZero", "zero")):
            command = request_example(spec, request_name)
            for _ in range(2):
                reply = client.post("/internal/messages", headers=SERVICE_HEADERS, json=command)
                assert reply.status_code == 200
                assert reply.json() == examples(write_op)[reply_name]["value"]
            status = client.get("/internal/messages/" + command["messageId"], headers=SERVICE_HEADERS)
            assert status.status_code == 404
            assert status.json() == examples(operation(spec, "/internal/messages/{message_id}"), 404)["error"]["value"]
        assert client.get("/v1/leaderboards/run-xp").json() == examples(ranking_op)["players"]["value"]
        assert client.get("/v1/leaderboards/run-xp/me", headers=player_headers).json() == examples(me_op)["ranked"]["value"]
        command = request_example(spec)
        invalid = deepcopy(command)
        invalid['schemaVersion'] = '1'
        response = client.post('/internal/messages', headers=SERVICE_HEADERS, json=invalid)
        assert response.status_code == 422
        assert response.json() == examples(write_op, 422)['invalidVersion']['value']
        invalid['schemaVersion'] = 2
        response = client.post('/internal/messages', headers=SERVICE_HEADERS, json=invalid)
        assert response.status_code == 422
        assert response.json() == examples(write_op, 422)['invalidContract']['value']
        changed = deepcopy(command)
        changed["payload"]["points"] = 81
        conflict = client.post("/internal/messages", headers=SERVICE_HEADERS, json=changed)
        assert conflict.status_code == 409
        assert conflict.json() == examples(write_op, 409)["error"]["value"]
        changed = deepcopy(command)
        changed["messageId"], changed["payload"]["runId"] = str(uuid4()), str(uuid4())
        changed["payload"]["points"] = 20
        assert client.post("/internal/messages", headers=SERVICE_HEADERS, json=changed).json()["receipt"]["scoreAfter"] == 100
        assert client.post("/internal/messages", headers=SERVICE_HEADERS, json=command).json() == examples(write_op)["applied"]["value"]
        assert client.get("/v1/leaderboards/run-xp/me", headers=player_headers).json()["entry"]["score"] == 100
        with rating_storage[0].engine.begin() as connection:
            connection.execute(text("UPDATE alembic_version SET version_num='unexpected'"))
        failure = client.get('/health/ready', headers={'X-Request-ID': REQUEST_ID})
        assert failure.status_code == 503
        assert failure.headers['Retry-After'] == '1'
        assert failure.json() == examples(operation(spec, '/health/ready'), 503)['revisionMismatch']['value']


@pytest.mark.parametrize("state", ["pending", "applied", "rejected", "review"])
def test_broker_status_examples_match_persisted_states(rating_storage, state):
    database, url = rating_storage
    uow = PostgresUnitOfWorkFactory(database)
    rating = RatingUseCases(uow)

    class Unavailable:
        def receive(self, *args):
            raise ApplicationError(503, "Temporary failure")

    class ConfirmedPublisher:
        def publish(self, event):
            pass

    with TestClient(create_app(Settings(url, SECRET))) as client:
        spec = client.get("/openapi.json").json()
        command = request_example(spec)
        if state == "rejected":
            command["eventName"] = "unsupported.event"
        AcceptBrokerDelivery(uow).execute(json.dumps(command).encode())
        if state == "review":
            with database.transaction() as session:
                session.execute(update(BrokerInbox).values(attempts=19, available_at=func.now()))
        if state != "pending":
            assert ProcessBrokerCommands(uow, Unavailable() if state == "review" else rating).once()
        if state == "applied":
            assert PublishBrokerEvents(uow, ConfirmedPublisher()).once()
        reply = client.get("/internal/messages/" + command["messageId"], headers=SERVICE_HEADERS)
        assert reply.status_code == 200
        assert reply.json() == examples(operation(spec, "/internal/messages/{message_id}"))[state]["value"]
