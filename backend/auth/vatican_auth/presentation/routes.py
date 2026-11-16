from uuid import UUID

from dishka.integrations.fastapi import FromDishka
from fastapi import APIRouter, Depends, Query, Request, Response
from fastapi.security import HTTPBearer
from sqlalchemy import text

from ..application.catalog import PERMISSION_MAP
from ..application.access import (
    AuthorizeUseCase, CreatePermissionUseCase, CreateRoleUseCase, GrantRoleUseCase,
    ListAuditUseCase, ListPermissionsUseCase, ListRolesUseCase, RevokeRoleUseCase, UpdateRoleUseCase,
)
from ..application.use_cases import (
    CreateGuestUseCase, LoginUseCase, LogoutUseCase, RecoverUseCase, RefreshUseCase, RegisterUseCase,
)
from ..migrate import SCHEMA_REVISION
from ..persistence.postgres.database import Database
from .access import AuthorizedRoute, bearer_token, require_permission
from .errors import SchemaNotReady
from .schemas import (
    AccessResponse, AuditResponse, ChangedResponse, CredentialResponse, CredentialsRequest,
    GuestResponse, LogoutRequest, PermissionCreateRequest, PermissionResponse, RecoverRequest,
    RefreshRequest, RegisterRequest, RoleCreateRequest, RoleResponse, RoleUpdateRequest, SessionResponse,
)

router = APIRouter(route_class=AuthorizedRoute)
bearer_scheme = HTTPBearer(auto_error=False, scheme_name="BearerAuth")
admin = APIRouter(prefix="/v1/admin", tags=["access"], route_class=AuthorizedRoute,
                  dependencies=[Depends(bearer_scheme)])


def client_ip(request: Request) -> str:
    # The entry point disables proxy-header rewriting; forwarded headers are not trusted here.
    return request.client.host if request.client else "0.0.0.0"


@router.get("/health/live", tags=["health"])
async def live():
    return {"status": "live"}


@router.get("/health/ready", tags=["health"])
async def ready(database: FromDishka[Database]):
    async with database.engine.connect() as connection:
        revision = await connection.scalar(text("SELECT version_num FROM alembic_version"))
    if revision != SCHEMA_REVISION:
        raise SchemaNotReady()
    return {"status": "ready", "schemaRevision": revision}


@router.post("/v1/guests", response_model=GuestResponse, status_code=201, tags=["auth"])
async def create_guest(request: Request, use_case: FromDishka[CreateGuestUseCase]):
    return await use_case.execute(client_ip(request))


@router.post("/v1/auth/register", response_model=CredentialResponse, status_code=201, tags=["auth"])
async def register(body: RegisterRequest, request: Request, use_case: FromDishka[RegisterUseCase]):
    result = await use_case.execute(body.login, body.password.get_secret_value(), client_ip(request),
                                    body.guest_token.get_secret_value() if body.guest_token else None)
    return CredentialResponse.from_credentials(result)


@router.post("/v1/auth/login", response_model=SessionResponse, tags=["auth"])
async def login(body: CredentialsRequest, request: Request, use_case: FromDishka[LoginUseCase]):
    return SessionResponse.from_session(await use_case.execute(body.login, body.password.get_secret_value(), client_ip(request)))


@router.post("/v1/auth/refresh", response_model=SessionResponse, tags=["auth"])
async def refresh(body: RefreshRequest, use_case: FromDishka[RefreshUseCase]):
    return SessionResponse.from_session(await use_case.execute(body.refresh_token.get_secret_value()))


@router.post("/v1/auth/recover", response_model=CredentialResponse, tags=["auth"])
async def recover(body: RecoverRequest, request: Request, use_case: FromDishka[RecoverUseCase]):
    result = await use_case.execute(body.login, body.password.get_secret_value(), body.recovery_code.get_secret_value(), client_ip(request))
    return CredentialResponse.from_credentials(result)


@router.post("/v1/auth/logout", status_code=204, tags=["auth"], dependencies=[Depends(bearer_scheme)])
async def logout(request: Request, use_case: FromDishka[LogoutUseCase], body: LogoutRequest | None = None):
    await use_case.execute(bearer_token(request), all_sessions=body.all_sessions if body else False)
    return Response(status_code=204)


@router.get("/v1/auth/me", response_model=AccessResponse, tags=["auth"], dependencies=[Depends(bearer_scheme)])
async def me(request: Request, use_case: FromDishka[AuthorizeUseCase]):
    return await use_case.execute(bearer_token(request))


@admin.get("/roles", response_model=list[RoleResponse])
@require_permission(PERMISSION_MAP["roles_read"].code)
async def roles(request: Request, use_case: FromDishka[ListRolesUseCase]):
    return await use_case.execute(bearer_token(request))


@admin.post("/roles", response_model=RoleResponse, status_code=201)
@require_permission(PERMISSION_MAP["roles_create"].code)
async def create_role(body: RoleCreateRequest, request: Request, use_case: FromDishka[CreateRoleUseCase]):
    return await use_case.execute(bearer_token(request), body.code, body.description, tuple(body.permissions), body.is_assignable)


@admin.put("/roles/{role_id}", response_model=RoleResponse)
@require_permission(PERMISSION_MAP["roles_update"].code)
async def update_role(role_id: int, body: RoleUpdateRequest, request: Request, use_case: FromDishka[UpdateRoleUseCase]):
    return await use_case.execute(bearer_token(request), role_id, body.description, tuple(body.permissions), body.is_assignable)


@admin.get("/permissions", response_model=list[PermissionResponse])
@require_permission(PERMISSION_MAP["permissions_read"].code)
async def permissions(request: Request, use_case: FromDishka[ListPermissionsUseCase]):
    return await use_case.execute(bearer_token(request))


@admin.post("/permissions", response_model=PermissionResponse, status_code=201)
@require_permission(PERMISSION_MAP["permissions_manage"].code)
async def create_permission(body: PermissionCreateRequest, request: Request, use_case: FromDishka[CreatePermissionUseCase]):
    return await use_case.execute(bearer_token(request), body.code, body.description)


@admin.put("/accounts/{account_id}/roles/{role_id}", response_model=ChangedResponse)
@require_permission(PERMISSION_MAP["roles_assign"].code)
async def grant_role(account_id: UUID, role_id: int, request: Request, use_case: FromDishka[GrantRoleUseCase]):
    return {"changed": await use_case.execute(bearer_token(request), account_id, role_id)}


@admin.delete("/accounts/{account_id}/roles/{role_id}", response_model=ChangedResponse)
@require_permission(PERMISSION_MAP["roles_revoke"].code)
async def revoke_role(account_id: UUID, role_id: int, request: Request, use_case: FromDishka[RevokeRoleUseCase]):
    return {"changed": await use_case.execute(bearer_token(request), account_id, role_id)}


@admin.get("/audit", response_model=list[AuditResponse])
@require_permission(PERMISSION_MAP["audit_read"].code)
async def audit(request: Request, use_case: FromDishka[ListAuditUseCase],
                limit: int = Query(default=50, ge=1, le=100), offset: int = Query(default=0, ge=0)):
    return await use_case.execute(bearer_token(request), limit, offset)


router.include_router(admin)
