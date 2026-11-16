from dataclasses import replace
from uuid import uuid4

from alembic import command
from fastapi import APIRouter
from fastapi.testclient import TestClient
import pytest
from sqlalchemy import func, select

from vatican_auth.presentation.app import create_app
from vatican_auth.application.access import BootstrapAdminUseCase, CreateRoleUseCase
from vatican_auth.application.errors import PermissionDenied
from vatican_auth.application.policy import AuthPolicy
from vatican_auth.migrate import SCHEMA_REVISION, migrate, migration_config
from vatican_auth.persistence.postgres import Base
from vatican_auth.persistence.postgres.repositories.audit import PostgresAuditRepository
from vatican_auth.persistence.postgres.tables import AccountRole, AdminAuditLog, Role
from vatican_auth.presentation.access import AuthorizedRoute, require_roles
from vatican_auth.bootstrap.settings import AuthSettings

PASSWORD = "correct-password-123"


@pytest.fixture()
def client(storage):
    settings = AuthSettings(storage.url.render_as_string(hide_password=False))
    app = create_app(settings)
    role_router = APIRouter(route_class=AuthorizedRoute)

    @role_router.get("/test/admin-role")
    @require_roles("ADMIN")
    async def admin_role():
        return {"allowed": True}

    app.include_router(role_router)
    with TestClient(app, client=("127.0.0.1", 12345), raise_server_exceptions=False) as client:
        yield client


def register(client, login="player"):
    response = client.post("/v1/auth/register", json={"login": login, "password": PASSWORD})
    assert response.status_code == 201, response.text
    return response.json()


def headers(session):
    return {"Authorization": "Bearer " + session["accessToken"]}


def invoke(client, use_case, *args, **kwargs):
    async def run():
        async with client.app.state.dishka_container() as operation:
            return await (await operation.get(use_case)).execute(*args, **kwargs)
    return client.portal.call(run)


def admin_account(client):
    account = register(client, "admin_user")
    invoke(client, BootstrapAdminUseCase, "admin_user")
    return account


def create_role(client, admin, code="READER", permissions=None, is_assignable=True):
    return client.post("/v1/admin/roles", headers=headers(admin), json={
        "code": code, "description": "Test role", "permissions": permissions or ["auth.roles.read"],
        "isAssignable": is_assignable,
    })


def test_http_auth_guest_registration_login_refresh_logout_and_openapi(client):
    assert client.get("/health/live").status_code == 200
    assert client.get("/health/ready").json()["schemaRevision"] == SCHEMA_REVISION
    guest = client.post("/v1/guests").json()
    response = client.post("/v1/auth/register", json={"login": "Player_One", "password": PASSWORD, "guestToken": guest["token"]})
    assert response.status_code == 201, response.text
    session = response.json()
    assert session["accountId"] == guest["accountId"]
    assert session["tokenType"] == "Bearer"
    assert response.headers["cache-control"] == "no-store"
    assert client.get("/v1/auth/me", headers={"Authorization": "Bearer " + guest["token"]}).status_code == 401
    me = client.get("/v1/auth/me", headers=headers(session))
    assert me.status_code == 200, me.text
    assert me.json()["roles"] == [] and me.json()["permissions"] == []
    logged = client.post("/v1/auth/login", json={"login": "PLAYER_ONE", "password": PASSWORD})
    assert logged.status_code == 200
    refreshed = client.post("/v1/auth/refresh", json={"refreshToken": session["refreshToken"]})
    assert refreshed.status_code == 200, refreshed.text
    assert client.post("/v1/auth/refresh", json={"refreshToken": session["refreshToken"]}).status_code == 401
    assert client.post("/v1/auth/logout", headers=headers(refreshed.json())).status_code == 204
    assert client.get("/v1/auth/me", headers=headers(refreshed.json())).status_code == 401
    schema = client.get("/openapi.json").json()
    assert "BearerAuth" in schema["components"]["securitySchemes"]
    assert schema["paths"]["/v1/admin/roles"]["get"]["security"] == [{"BearerAuth": []}]
    assert not any("bootstrap" in path for path in schema["paths"])


def test_http_recovery_revokes_sessions_and_rotates_recovery_code(client):
    first = register(client)
    response = client.post("/v1/auth/recover", json={"login": "player", "password": "new-password-456",
                                                   "recoveryCode": first["recoveryCode"]})
    assert response.status_code == 200, response.text
    assert response.json()["recoveryCode"] != first["recoveryCode"]
    assert client.get("/v1/auth/me", headers=headers(first)).status_code == 401
    assert client.post("/v1/auth/login", json={"login": "player", "password": PASSWORD}).status_code == 401
    assert client.post("/v1/auth/login", json={"login": "player", "password": "new-password-456"}).status_code == 200
    assert client.post("/v1/auth/recover", json={"login": "player", "password": PASSWORD,
                                               "recoveryCode": first["recoveryCode"]}).status_code == 401


def test_validation_does_not_echo_secrets_and_errors_have_expected_status(client):
    secret = "sensitive-password-that-must-not-be-echoed"
    response = client.post("/v1/auth/register", json={"login": "invalid login", "password": secret, "extra": secret})
    assert response.status_code == 422
    assert secret not in response.text
    response = client.post("/v1/auth/recover", json={"login": "player", "password": secret, "recoveryCode": secret})
    assert response.status_code == 422 and secret not in response.text
    assert client.get("/v1/auth/me").status_code == 401
    assert client.get("/v1/auth/me", headers={"Authorization": "Bearer invalid"}).headers["www-authenticate"] == "Bearer"
    register(client)
    assert client.post("/v1/auth/register", json={"login": "PLAYER", "password": PASSWORD}).status_code == 409
    assert client.post("/v1/auth/login", json={"login": "player", "password": "wrong"}).status_code == 401


def test_http_rate_limit_returns_retry_after_and_ignores_forwarded_ip(storage):
    policy = replace(AuthPolicy(), ip_attempt_limit=1)
    app = create_app(AuthSettings(storage.url.render_as_string(hide_password=False), policy=policy))
    with TestClient(app, client=("127.0.0.1", 12345)) as client:
        assert client.post("/v1/guests", headers={"X-Forwarded-For": "1.1.1.1"}).status_code == 201
        response = client.post("/v1/guests", headers={"X-Forwarded-For": "2.2.2.2"})
        assert response.status_code == 429
        assert 1 <= int(response.headers["retry-after"]) <= 300


def test_regular_accounts_and_guests_cannot_self_assign_admin(client, storage):
    user = register(client)
    guest = client.post("/v1/guests").json()
    for token in [user["accessToken"], guest["token"]]:
        auth = {"Authorization": "Bearer " + token}
        assert client.get("/v1/admin/roles", headers=auth).status_code == 403
        assert client.put(f"/v1/admin/accounts/{user['accountId']}/roles/1", headers=auth).status_code == 403
        assert client.get("/test/admin-role", headers=auth).status_code == 403
    with storage.connect() as connection:
        assert connection.scalar(select(func.count()).select_from(AccountRole)) == 0
        denied = connection.execute(select(AdminAuditLog.details).where(AdminAuditLog.result == "DENIED")).all()
        assert denied and all("token" not in str(row) for row in denied)


def test_bootstrap_admin_is_idempotent_and_roles_decorator_is_enforced(client, storage):
    admin = admin_account(client)
    invoke(client, BootstrapAdminUseCase, "admin_user")
    me = client.get("/v1/auth/me", headers=headers(admin)).json()
    assert me["roles"] == ["ADMIN"] and "auth.roles.assign" in me["permissions"]
    assert client.get("/test/admin-role", headers=headers(admin)).status_code == 200
    with storage.connect() as connection:
        assert connection.scalar(select(func.count()).select_from(AccountRole).where(AccountRole.revoked_at.is_(None))) == 1
    assert client.get("/v1/admin/roles").status_code == 401


def test_role_grant_revoke_and_regrant_keep_history_and_change_access_immediately(client, storage):
    admin = admin_account(client)
    user = register(client, "reader_user")
    response = create_role(client, admin)
    assert response.status_code == 201, response.text
    role_id = response.json()["id"]
    endpoint = f"/v1/admin/accounts/{user['accountId']}/roles/{role_id}"
    assert client.put(endpoint, headers=headers(admin)).json() == {"changed": True}
    assert client.put(endpoint, headers=headers(admin)).json() == {"changed": False}
    assert client.get("/v1/admin/roles", headers=headers(user)).status_code == 200
    assert create_role(client, user, "FORBIDDEN").status_code == 403
    assert client.delete(endpoint, headers=headers(admin)).json() == {"changed": True}
    assert client.get("/v1/admin/roles", headers=headers(user)).status_code == 403
    assert client.put(endpoint, headers=headers(admin)).status_code == 200
    with storage.connect() as connection:
        assert connection.scalar(select(func.count()).select_from(AccountRole).where(AccountRole.role_id == role_id)) == 2


def test_role_update_permission_creation_and_nonassignable_role(client):
    admin = admin_account(client)
    user = register(client, "target_user")
    permission = client.post("/v1/admin/permissions", headers=headers(admin), json={
        "code": "shop.products.write", "description": "Manage shop products",
    })
    assert permission.status_code == 201, permission.text
    assert "shop.products.write" in client.get("/v1/auth/me", headers=headers(admin)).json()["permissions"]
    role = create_role(client, admin, "SHOP_MANAGER", ["shop.products.write"], is_assignable=False).json()
    endpoint = f"/v1/admin/accounts/{user['accountId']}/roles/{role['id']}"
    assert client.put(endpoint, headers=headers(admin)).status_code == 409
    response = client.put(f"/v1/admin/roles/{role['id']}", headers=headers(admin), json={
        "description": "Updated role", "permissions": ["shop.products.write", "auth.roles.read"], "isAssignable": True,
    })
    assert response.status_code == 200, response.text
    assert client.put(endpoint, headers=headers(admin)).status_code == 200
    assert "shop.products.write" in client.get("/v1/auth/me", headers=headers(user)).json()["permissions"]
    assert client.get("/v1/admin/permissions", headers=headers(admin)).status_code == 200


def test_limited_operator_cannot_delegate_or_edit_more_powerful_roles(client):
    admin = admin_account(client)
    operator = register(client, "operator_user")
    role = create_role(client, admin, "OPERATOR", ["auth.roles.create", "auth.roles.update", "auth.roles.assign", "auth.roles.revoke"]).json()
    assert client.put(f"/v1/admin/accounts/{operator['accountId']}/roles/{role['id']}", headers=headers(admin)).status_code == 200
    assert create_role(client, operator, "ESCALATED", ["auth.audit.read"]).status_code == 403
    admin_role = next(role for role in client.get("/v1/admin/roles", headers=headers(admin)).json() if role["code"] == "ADMIN")
    assert client.put(f"/v1/admin/accounts/{operator['accountId']}/roles/{admin_role['id']}", headers=headers(operator)).status_code == 403
    powerful = create_role(client, admin, "POWERFUL", ["auth.audit.read"]).json()
    response = client.put(f"/v1/admin/roles/{powerful['id']}", headers=headers(operator), json={
        "description": "Steal existing role", "permissions": ["auth.roles.create"],
    })
    assert response.status_code == 403
    # Direct application calls enforce the same policy, independently of HTTP decorators.
    with pytest.raises(PermissionDenied):
        invoke(client, CreateRoleUseCase, operator["accessToken"], "DIRECT_ESCALATION", "Bad role", ("auth.audit.read",))


def test_last_admin_is_protected_and_builtin_role_cannot_be_redefined(client):
    admin = admin_account(client)
    admin_role = next(role for role in client.get("/v1/admin/roles", headers=headers(admin)).json() if role["code"] == "ADMIN")
    endpoint = f"/v1/admin/accounts/{admin['accountId']}/roles/{admin_role['id']}"
    assert client.delete(endpoint, headers=headers(admin)).status_code == 409
    assert client.put(f"/v1/admin/roles/{admin_role['id']}", headers=headers(admin), json={
        "description": "Empty admin", "permissions": [], "isAssignable": False,
    }).status_code == 409
    second = register(client, "second_admin")
    assert client.put(f"/v1/admin/accounts/{second['accountId']}/roles/{admin_role['id']}", headers=headers(admin)).status_code == 200
    assert client.delete(endpoint, headers=headers(second)).json() == {"changed": True}
    assert client.get("/v1/admin/roles", headers=headers(admin)).status_code == 403
    assert client.delete(f"/v1/admin/accounts/{second['accountId']}/roles/{admin_role['id']}", headers=headers(second)).status_code == 409


def test_audit_failure_rolls_back_role_creation(client, storage, monkeypatch):
    admin = admin_account(client)
    original = PostgresAuditRepository.append
    async def fail(repository, actor_id, target_id, action, result, details):
        if action == "auth.roles.create" and result == "SUCCEEDED":
            raise RuntimeError("injected audit failure")
        return await original(repository, actor_id, target_id, action, result, details)
    with monkeypatch.context() as patch:
        patch.setattr(PostgresAuditRepository, "append", fail)
        assert create_role(client, admin, "ROLLED_BACK").status_code == 500
    with storage.connect() as connection:
        assert connection.scalar(select(Role.id).where(Role.code == "ROLLED_BACK")) is None
    response = client.get("/v1/admin/audit", headers=headers(admin))
    assert response.status_code == 200
    assert any(row["action"] == "auth.bootstrap_admin" for row in response.json())
    assert client.get("/v1/admin/audit?limit=101", headers=headers(admin)).status_code == 422


def test_missing_role_permission_or_account_returns_404_without_partial_mutation(client):
    admin = admin_account(client)
    assert create_role(client, admin, "BAD_PERMISSION", ["does.not.exist"]).status_code == 404
    assert client.put(f"/v1/admin/accounts/{uuid4()}/roles/9999", headers=headers(admin)).status_code == 404
    audit = client.get("/v1/admin/audit", headers=headers(admin))
    assert audit.status_code == 200
    assert any(row["result"] == "DENIED" for row in audit.json())


def test_auth_migration_matches_models_and_is_reversible_and_repeatable(storage):
    url = storage.url.render_as_string(hide_password=False)
    migrate(url)
    with storage.begin() as connection:
        config = migration_config()
        config.attributes["connection"] = connection
        command.check(config)
        command.downgrade(config, "base")
        from sqlalchemy import inspect
        assert inspect(connection).get_table_names() == ["alembic_version"]
        command.upgrade(config, "head")
        command.check(config)
        assert set(inspect(connection).get_table_names()) == set(Base.metadata.tables) | {"alembic_version"}


def test_environment_settings_encode_database_secrets_and_validate_policy():
    settings = AuthSettings.from_environment({"VD_AUTH_DB_PASSWORD": "p@ss:/?#", "VD_AUTH_DB_USER": "auth_user"})
    from sqlalchemy.engine import make_url
    assert make_url(settings.database_url).password == "p@ss:/?#"
    assert "p@ss" not in repr(settings)
    with pytest.raises(ValueError):
        AuthSettings.from_environment({})
    with pytest.raises(ValueError):
        AuthSettings.from_environment({"VD_AUTH_DATABASE_URL": "postgresql://localhost/auth", "VD_AUTH_ACCESS_TTL_SECONDS": "0"})


def test_business_failure_after_role_write_rolls_back_changes_but_keeps_denial_audit(client, storage, monkeypatch):
    from vatican_auth.persistence.postgres.repositories.access import PostgresAccessRepository

    admin = admin_account(client)
    original = PostgresAccessRepository.create_role
    async def fail_after_write(repository, *args, **kwargs):
        await original(repository, *args, **kwargs)
        raise PermissionDenied("Private diagnostic message")
    with monkeypatch.context() as patch:
        patch.setattr(PostgresAccessRepository, "create_role", fail_after_write)
        response = create_role(client, admin, "BUSINESS_ROLLBACK")
        assert response.status_code == 403
        assert "Private diagnostic" not in response.text
    with storage.connect() as connection:
        assert connection.scalar(select(Role.id).where(Role.code == "BUSINESS_ROLLBACK")) is None
        assert connection.scalar(select(AdminAuditLog.id).where(
            AdminAuditLog.action == "auth.roles.create", AdminAuditLog.result == "DENIED",
        )) is not None


def test_password_policy_failure_names_field_and_returns_configured_limits(client):
    response = client.post("/v1/auth/register", json={"login": "player", "password": "short"})
    assert response.status_code == 400
    body = response.json()
    assert body["code"] == "PasswordPolicyViolation"
    assert "8" in body["detail"] and "128" in body["detail"]
    assert body["errors"][0]["field"] == "password"
    assert "short" not in response.text


def test_additional_builtin_roles_follow_catalog_policy_without_repository_special_cases(client, monkeypatch):
    from types import MappingProxyType
    from vatican_auth.application import access
    from vatican_auth.application.catalog import RoleDefinition

    configured = {
        **access.ROLE_MAP,
        "support_lead": RoleDefinition("SUPPORT_LEAD", "Support lead", protected=True,
                                       grants_all_permissions=True, minimum_active_accounts=1),
        "catalog_reader": RoleDefinition("CATALOG_READER", "Catalog reader", permissions=("auth.roles.read",)),
    }
    monkeypatch.setattr(access, "ROLE_MAP", MappingProxyType(configured))
    monkeypatch.setattr(access, "ROLE_BY_CODE", MappingProxyType({role.code: role for role in configured.values()}))
    admin = admin_account(client)
    support = register(client, "support_user")
    reader = register(client, "catalog_reader")
    roles = {role["code"]: role for role in client.get("/v1/admin/roles", headers=headers(admin)).json()}
    support_role = roles["SUPPORT_LEAD"]
    endpoint = f"/v1/admin/accounts/{support['accountId']}/roles/{support_role['id']}"
    assert client.put(endpoint, headers=headers(admin)).status_code == 200
    response = client.delete(endpoint, headers=headers(admin))
    assert response.status_code == 409 and "SUPPORT_LEAD" in response.json()["detail"]
    response = client.put(f"/v1/admin/roles/{support_role['id']}", headers=headers(admin), json={
        "description": "Changed protected role", "permissions": [],
    })
    assert response.status_code == 409 and "SUPPORT_LEAD" in response.json()["detail"]
    assert client.post("/v1/admin/permissions", headers=headers(admin), json={
        "code": "support.tickets.write", "description": "Manage tickets",
    }).status_code == 201
    assert "support.tickets.write" in client.get("/v1/auth/me", headers=headers(support)).json()["permissions"]
    assert client.put(f"/v1/admin/accounts/{reader['accountId']}/roles/{roles['CATALOG_READER']['id']}", headers=headers(admin)).status_code == 200
    assert client.get("/v1/admin/roles", headers=headers(reader)).status_code == 200
    assert client.get("/v1/admin/permissions", headers=headers(reader)).status_code == 403
