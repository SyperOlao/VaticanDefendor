"""Verify that executable Swagger examples stay consistent with the HTTP contract."""
from copy import deepcopy
from dataclasses import replace
from datetime import timedelta

from fastapi.testclient import TestClient
import pytest
from pydantic import TypeAdapter

from vatican_auth.application.catalog import PERMISSION_MAP
from vatican_auth.application.policy import AuthPolicy
from vatican_auth.bootstrap.grpc import AuthGrpcSettings
from vatican_auth.bootstrap.settings import AuthSettings
from vatican_auth.presentation import schemas
from vatican_auth.presentation.app import create_app
from vatican_auth.presentation.documentation import ENDPOINTS, PERMISSION_KEYS, TAG_METADATA


@pytest.fixture(scope="module")
def spec():
    return create_app(AuthSettings("postgresql://unused@localhost/auth"), grpc_settings=AuthGrpcSettings()).openapi()


def operations(spec):
    for path, methods in spec["paths"].items():
        for method, operation in methods.items():
            yield path, method, operation


def resolve_adapter(schema):
    if "$ref" in schema:
        name = schema["$ref"].rsplit("/", 1)[1]
        return TypeAdapter(getattr(schemas, name))
    if schema.get("type") == "array":
        name = schema["items"]["$ref"].rsplit("/", 1)[1]
        return TypeAdapter(list[getattr(schemas, name)])
    return None


def test_every_endpoint_explains_behavior_and_provides_success_examples(spec):
    entries = list(operations(spec))
    assert len(entries) == len(ENDPOINTS) == 17
    known_tags = {tag["name"] for tag in TAG_METADATA}
    for path, method, operation in entries:
        assert operation["summary"] and len(operation["description"]) > 50, (method, path)
        assert set(operation["tags"]) <= known_tags
        success = next(response for status, response in operation["responses"].items() if status.startswith("2"))
        if "204" in operation["responses"]:
            assert "content" not in success
        else:
            examples = success["content"]["application/json"]["examples"]
            assert examples and all(example["summary"] for example in examples.values())
        for status, response in operation["responses"].items():
            if status.startswith(("4", "5")):
                media = response["content"]["application/json"]
                assert media["schema"]["$ref"].endswith("/ErrorResponse")
                for example in media["examples"].values():
                    schemas.ErrorResponse.model_validate(example["value"])


def test_all_request_and_response_examples_validate_against_their_models(spec):
    request_examples = response_examples = 0
    for path, method, operation in operations(spec):
        body = operation.get("requestBody")
        if body:
            media = body["content"]["application/json"]
            assert media["examples"], (method, path)
            # Optional logout uses anyOf(Model, null).
            schema = media["schema"]
            if "anyOf" in schema:
                schema = next(option for option in schema["anyOf"] if "$ref" in option)
            adapter = resolve_adapter(schema)
            assert adapter is not None, (method, path)
            for example in media["examples"].values():
                adapter.validate_python(example["value"])
                request_examples += 1
        for status, response in operation["responses"].items():
            if not status.startswith("2") or status == "204":
                continue
            media = response["content"]["application/json"]
            adapter = resolve_adapter(media.get("schema", {}))
            if adapter is not None:
                for example in media["examples"].values():
                    adapter.validate_python(example["value"])
                    response_examples += 1
    assert request_examples >= 11 and response_examples >= 15


def test_fields_explain_tokens_identifiers_and_secrets_are_write_only(spec):
    for name in ["RegisterRequest", "CredentialsRequest", "RefreshRequest", "RecoverRequest", "LogoutRequest",
                 "GuestResponse", "SessionResponse", "CredentialResponse", "AccessResponse", "RoleCreateRequest",
                 "RoleUpdateRequest", "RoleResponse", "PermissionCreateRequest", "PermissionResponse", "AuditResponse", "ErrorResponse"]:
        for field, definition in spec["components"]["schemas"][name]["properties"].items():
            assert definition.get("description"), (name, field)
    assert spec["components"]["schemas"]["CredentialsRequest"]["properties"]["password"]["writeOnly"]
    assert spec["components"]["schemas"]["RefreshRequest"]["properties"]["refreshToken"]["writeOnly"]
    assert spec["paths"]["/v1/auth/logout"]["post"]["requestBody"].get("required", False) is False


def test_security_icons_required_permissions_and_path_parameters_are_clear(spec):
    for path, method, operation in operations(spec):
        protected = path.startswith("/v1/admin/") or path in {"/v1/auth/me", "/v1/auth/logout"}
        assert bool(operation.get("security")) == protected, (method, path)
        if path.startswith("/v1/admin/"):
            assert any(definition.code in operation["description"]
                       for key, definition in PERMISSION_MAP.items() if key in PERMISSION_KEYS.values())
        for parameter in operation.get("parameters", []):
            assert parameter.get("description"), (path, parameter)
    assert set(spec["paths"]["/health/live"]["get"]["responses"]) == {"200", "500", "503"}
    assert "403" not in spec["paths"]["/v1/auth/register"]["post"]["responses"]
    assert spec["components"]["securitySchemes"]["BearerAuth"]["description"]


def test_intro_uses_configured_policy_instead_of_hardcoded_password_limits():
    policy = replace(AuthPolicy(), password_min_length=12, password_max_length=32,
                     access_ttl=timedelta(seconds=60), ip_attempt_limit=40)
    spec = create_app(AuthSettings("postgresql://unused@localhost/auth", policy=policy),
                      grpc_settings=AuthGrpcSettings()).openapi()
    description = spec["info"]["description"]
    assert "12–32" in description and "60 секунд" in description and "40" in description
    assert "без" in description and "Bearer" in description
    assert "вымышленные" in description


def test_primary_swagger_examples_can_complete_a_real_auth_flow(storage):
    app = create_app(AuthSettings(storage.url.render_as_string(hide_password=False)), grpc_settings=AuthGrpcSettings())
    with TestClient(app, client=("127.0.0.1", 12345)) as client:
        assert client.get("/docs").status_code == 200
        spec = client.get("/openapi.json").json()
        assert client.get("/health/ready").status_code == 200
        def body(path, example):
            return deepcopy(spec["paths"][path]["post"]["requestBody"]["content"]["application/json"]["examples"][example]["value"])
        registration = body("/v1/auth/register", "new_account")
        response = client.post("/v1/auth/register", json=registration)
        assert response.status_code == 201, response.text
        account = response.json()
        login = client.post("/v1/auth/login", json=body("/v1/auth/login", "password"))
        assert login.status_code == 200
        refresh = body("/v1/auth/refresh", "rotate")
        refresh["refreshToken"] = account["refreshToken"]
        refreshed = client.post("/v1/auth/refresh", json=refresh)
        assert refreshed.status_code == 200
        token = refreshed.json()["accessToken"]
        me = client.get("/v1/auth/me", headers={"Authorization": "Bearer " + token})
        assert me.json()["accountId"] == account["accountId"] and me.json()["roles"] == []
        recover = body("/v1/auth/recover", "reset")
        recover["recoveryCode"] = account["recoveryCode"]
        recovered = client.post("/v1/auth/recover", json=recover)
        assert recovered.status_code == 200
        assert recovered.json()["recoveryCode"] != account["recoveryCode"]
        logout = client.post("/v1/auth/logout", json=body("/v1/auth/logout", "all_sessions"),
                             headers={"Authorization": "Bearer " + recovered.json()["accessToken"]})
        assert logout.status_code == 204 and not logout.content
        assert client.get("/v1/auth/me", headers={"Authorization": "Bearer " + token}).status_code == 401
