import asyncio
import logging
from types import SimpleNamespace
import traceback
from uuid import UUID, uuid4

from fastapi import HTTPException
from fastapi.testclient import TestClient
from pydantic import BaseModel, ConfigDict, SecretStr, field_validator
import pytest
from sqlalchemy.exc import DBAPIError, IntegrityError, OperationalError, ProgrammingError, TimeoutError
from starlette.requests import ClientDisconnect
from starlette.responses import StreamingResponse

from vatican_auth.presentation.app import create_app
from vatican_auth.application.errors import AuthError, InvalidInput, PermissionDenied
from vatican_auth.presentation.errors import ErrorBoundaryMiddleware, ResponseAlreadyStartedError, SchemaNotReady
from vatican_auth.bootstrap.settings import AuthSettings

SECRET = "must-never-appear-password-token-database-secret"


class DriverFailure(Exception):
    def __init__(self, state, constraint=None):
        self.sqlstate = state
        self.diag = SimpleNamespace(constraint_name=constraint)
        super().__init__(SECRET)


def database_error(error_type, state, constraint=None):
    return error_type("SELECT " + SECRET, {"password": SECRET}, DriverFailure(state, constraint))


class UnsafeValidator(BaseModel):
    model_config = ConfigDict(extra="forbid")
    password: SecretStr

    @field_validator("password")
    @classmethod
    def reject(cls, value):
        raise ValueError("Unsafe validator echoed " + value.get_secret_value())


class RequiredResponse(BaseModel):
    count: int


def app_with_error_routes():
    app = create_app(AuthSettings("postgresql://unused@127.0.0.1/unused"))
    factories = {
        "runtime": lambda: RuntimeError(SECRET),
        "unknown-domain": lambda: AuthError(SECRET),
        "invalid-input": lambda: InvalidInput(SECRET),
        "forbidden": lambda: PermissionDenied(SECRET),
        "connection": lambda: database_error(OperationalError, "08006"),
        "pool": lambda: TimeoutError(SECRET),
        "deadlock": lambda: database_error(OperationalError, "40P01"),
        "serialization": lambda: database_error(OperationalError, "40001"),
        "timeout": lambda: database_error(OperationalError, "57014"),
        "missing-table": lambda: database_error(ProgrammingError, "42P01"),
        "syntax": lambda: database_error(ProgrammingError, "42601"),
        "internal-db": lambda: database_error(OperationalError, "XX000"),
        "duplicate-login": lambda: database_error(IntegrityError, "23505", "account_logins_pkey"),
        "duplicate-account": lambda: database_error(IntegrityError, "23505", "account_logins_account_id_key"),
        "duplicate-role": lambda: database_error(IntegrityError, "23505", "roles_code_key"),
        "unknown-unique": lambda: database_error(IntegrityError, "23505", SECRET),
        "foreign-key": lambda: database_error(IntegrityError, "23503"),
        "check-constraint": lambda: database_error(IntegrityError, "23514"),
        "invalidated": lambda: DBAPIError("SELECT " + SECRET, {}, DriverFailure("XXXXX"), connection_invalidated=True),
        "schema": lambda: SchemaNotReady(SECRET),
        "http-500": lambda: HTTPException(500, {"password": SECRET}),
        "http-404": lambda: HTTPException(404, {"password": SECRET}),
        "http-401": lambda: HTTPException(401, SECRET, headers={"WWW-Authenticate": SECRET, "X-Error": SECRET}),
        "http-503": lambda: HTTPException(503, SECRET),
    }

    @app.get("/test/errors/{kind}")
    async def fail(kind: str):
        raise factories[kind]()

    @app.post("/test/unsafe-validation")
    async def validation(body: UnsafeValidator):
        return {}

    @app.get("/test/invalid-response", response_model=RequiredResponse)
    async def invalid_response():
        return {"count": SECRET}

    @app.get("/test/stream")
    async def stream():
        async def chunks():
            yield b"first chunk"
            raise RuntimeError(SECRET)
        return StreamingResponse(chunks())

    return app


@pytest.fixture(scope="module")
def client():
    with TestClient(app_with_error_routes(), client=("127.0.0.1", 12345)) as client:
        yield client


def assert_contract(response, status, code):
    assert response.status_code == status, response.text
    body = response.json()
    assert set(body) == {"code", "detail", "requestId", "errors"}
    assert body["code"] == code and isinstance(body["errors"], list)
    assert str(UUID(body["requestId"])) == response.headers["x-request-id"]
    assert response.headers["cache-control"] == "no-store"
    assert response.headers["pragma"] == "no-cache"
    assert SECRET not in response.text
    assert SECRET not in str(response.headers)
    return body


@pytest.mark.parametrize("kind,status,code", [
    ("runtime", 500, "InternalError"), ("unknown-domain", 500, "InternalError"),
    ("invalid-input", 400, "InvalidInput"), ("forbidden", 403, "PermissionDenied"),
    ("connection", 503, "DatabaseUnavailable"), ("pool", 503, "DatabaseUnavailable"),
    ("invalidated", 503, "DatabaseUnavailable"),
    ("deadlock", 409, "ConcurrentModification"), ("serialization", 409, "ConcurrentModification"),
    ("timeout", 503, "OperationTimeout"), ("missing-table", 503, "SchemaNotReady"),
    ("schema", 503, "SchemaNotReady"), ("syntax", 500, "InternalError"),
    ("internal-db", 500, "InternalError"), ("duplicate-login", 409, "LoginUnavailable"),
    ("duplicate-account", 409, "AccountAlreadyRegistered"), ("duplicate-role", 409, "ResourceConflict"),
    ("unknown-unique", 500, "InternalError"), ("foreign-key", 500, "InternalError"),
    ("check-constraint", 500, "InternalError"), ("http-500", 500, "InternalError"),
    ("http-404", 404, "RouteNotFound"), ("http-401", 401, "InvalidCredentials"),
    ("http-503", 503, "ServiceUnavailable"),
])
def test_error_classification_is_consistent_and_private(client, kind, status, code, caplog):
    with caplog.at_level(logging.WARNING, logger="vatican_auth.errors"):
        response = client.get("/test/errors/" + kind, params={"password": SECRET},
                              headers={"Authorization": "Bearer " + SECRET})
    body = assert_contract(response, status, code)
    if status == 503:
        assert response.headers["retry-after"] == "1"
    else:
        assert "retry-after" not in response.headers
    if status == 401:
        assert response.headers["www-authenticate"] == "Bearer"
    records = [record for record in caplog.records if record.name == "vatican_auth.errors"]
    assert bool(records) == (status >= 500)
    for record in records:
        assert record.request_id == body["requestId"]
        assert record.error_code == code and record.status_code == status
        assert record.exc_info is None
        assert SECRET not in record.getMessage() and SECRET not in repr(record.__dict__)


def test_404_and_405_use_the_same_contract_and_keep_allow_header(client):
    assert_contract(client.get("/not-a-real-route/" + SECRET), 404, "RouteNotFound")
    response = client.post("/health/live")
    assert_contract(response, 405, "MethodNotAllowed")
    assert "GET" in response.headers["allow"]


def test_invalid_json_validator_messages_and_unknown_field_names_do_not_echo_input(client):
    response = client.post("/v1/auth/register", content='{"password":"' + SECRET + '",}',
                           headers={"Content-Type": "application/json"})
    body = assert_contract(response, 422, "InvalidRequest")
    assert body["errors"][0]["type"] == "json_invalid"
    response = client.post("/test/unsafe-validation", json={"password": SECRET, SECRET: SECRET})
    body = assert_contract(response, 422, "InvalidRequest")
    assert body["errors"]
    assert all(set(issue) == {"field", "type", "message"} for issue in body["errors"])
    response = client.post("/test/unsafe-validation", json={"password": SECRET, **{SECRET + str(i): SECRET for i in range(100)}})
    assert len(assert_contract(response, 422, "InvalidRequest")["errors"]) == 50


def test_response_validation_failure_is_internal_and_does_not_echo_tokens(client, caplog):
    response = client.get("/test/invalid-response")
    assert_contract(response, 500, "InternalError")
    assert SECRET not in caplog.text
    assert any(record.exception_type == "ResponseValidationError" for record in caplog.records)


def test_request_id_is_canonical_or_generated_and_present_on_success(client):
    request_id = str(uuid4())
    response = client.get("/test/errors/runtime", headers={"X-Request-ID": request_id})
    assert assert_contract(response, 500, "InternalError")["requestId"] == request_id
    response = client.get("/test/errors/runtime", headers={"X-Request-ID": SECRET})
    assert assert_contract(response, 500, "InternalError")["requestId"] != SECRET
    first = client.get("/health/live")
    second = client.get("/health/live")
    assert UUID(first.headers["x-request-id"]) != UUID(second.headers["x-request-id"])
    assert first.headers["cache-control"] == "no-store"


def test_openapi_documents_the_actual_error_contract(client):
    schema = client.get("/openapi.json").json()
    for status in ["400", "401", "403", "404", "409", "422", "429", "500", "503"]:
        response = schema["paths"]["/v1/auth/register"]["post"]["responses"][status]
        assert response["content"]["application/json"]["schema"]["$ref"].endswith("/ErrorResponse")
        assert "X-Request-ID" in response["headers"]
    assert set(schema["components"]["schemas"]["ErrorResponse"]["properties"]) == {"code", "detail", "requestId", "errors"}


def test_failure_after_stream_starts_is_sanitized_and_never_sends_a_second_response(caplog):
    with TestClient(app_with_error_routes()) as client:
        with pytest.raises(ResponseAlreadyStartedError) as captured:
            client.get("/test/stream")
    assert SECRET not in "".join(traceback.format_exception(captured.value))
    assert SECRET not in caplog.text


def test_disconnects_and_cancellation_are_not_reported_as_internal_errors(caplog):
    async def run(error):
        async def fail(scope, receive, send):
            raise error
        async def unexpected(*args):
            raise AssertionError("No error response should be sent")
        app = ErrorBoundaryMiddleware(fail)
        await app({"type": "http", "headers": []}, unexpected, unexpected)
    asyncio.run(run(ClientDisconnect()))
    with pytest.raises(asyncio.CancelledError):
        asyncio.run(run(asyncio.CancelledError()))
    assert not [record for record in caplog.records if record.name == "vatican_auth.errors"]
