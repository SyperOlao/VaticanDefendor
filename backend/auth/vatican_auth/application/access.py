import re
from uuid import UUID

from .catalog import (
    ACCOUNT_STATUS_MAP, AUDIT_ACTION_MAP, AUDIT_RESULT_MAP, BOOTSTRAP_ROLE_KEY,
    PERMISSION_MAP, ROLE_BY_CODE, ROLE_MAP,
)
from .errors import (
    AuthError, DelegationDenied, InvalidInput, LastAdministrator, PermissionDenied,
    ProtectedRole, RegisteredAccountRequired, ResourceConflict, ResourceNotFound, RoleNotAssignable,
)
from .models import AccessSnapshot
from .ports import Clock, TokenService, UnitOfWorkFactory
from .services import authenticate_in_work, require_active
from .validation import normalize_login


async def seed_catalog(work):
    for definition in PERMISSION_MAP.values():
        await work.access.ensure_permission(definition.code, definition.description)
    all_codes = tuple(permission.code for permission in await work.access.permissions())
    roles = {}
    for key, definition in ROLE_MAP.items():
        role = await work.access.ensure_role(definition.code, definition.description, definition.is_assignable)
        codes = all_codes if definition.grants_all_permissions else definition.permissions
        if not set(codes).issubset(all_codes):
            raise InvalidInput("Built-in role references an unknown permission")
        await work.access.set_role_permissions(role.id, codes)
        roles[key] = await work.access.role(role.id)
    return roles


async def snapshot(work, principal) -> AccessSnapshot:
    roles = await work.access.roles_for(principal.account_id) if not principal.is_guest else ()
    permissions = await work.access.permissions_for(principal.account_id) if not principal.is_guest else ()
    return AccessSnapshot(principal.account_id, principal.session_id, principal.is_guest, roles, permissions)


def require_permissions(access: AccessSnapshot, *permissions: str) -> None:
    if access.is_guest or not set(permissions).issubset(access.permissions):
        raise PermissionDenied("Required permission is missing")


def description_value(value: str) -> str:
    if not isinstance(value, str) or not 1 <= len(value) <= 512:
        raise InvalidInput("Description must contain 1–512 characters")
    return value


class AuthorizeUseCase:
    def __init__(self, uow: UnitOfWorkFactory, tokens: TokenService, clock: Clock):
        self.uow, self.tokens, self.clock = uow, tokens, clock

    async def execute(self, access_token: str, permission: str | None = None,
                      roles: tuple[str, ...] = ()) -> AccessSnapshot:
        denied = None
        async with self.uow() as work:
            # Policy is read consistently with role and permission updates.
            await work.access.lock()
            principal = await authenticate_in_work(work, self.tokens, self.clock, access_token)
            access = await snapshot(work, principal)
            try:
                if permission is not None:
                    require_permissions(access, permission)
                if roles and (access.is_guest or not set(roles).intersection(access.roles)):
                    raise PermissionDenied("Required role is missing")
            except PermissionDenied as error:
                denied = error
                await work.audit.append(principal.account_id, None, AUDIT_ACTION_MAP["access_check"], AUDIT_RESULT_MAP["denied"],
                                        {"permission": permission, "roles": list(roles)})
        if denied is not None:
            raise denied
        return access


class AdminUseCase:
    def __init__(self, uow: UnitOfWorkFactory, tokens: TokenService, clock: Clock):
        self.uow, self.tokens, self.clock = uow, tokens, clock

    async def perform(self, token: str, permission: str, action: str, operation, *, target_id: UUID | None = None):
        error = None
        actor_id = None
        async with self.uow() as work:
            await work.access.lock()
            try:
                principal = await authenticate_in_work(work, self.tokens, self.clock, token, lock=True)
                actor_id = principal.account_id
                access = await snapshot(work, principal)
                require_permissions(access, permission)
                async with work.savepoint():
                    result, details = await operation(work, access)
            except AuthError as failure:
                error = failure
                # A missing target cannot be used as an audit FK.
                valid_target = target_id if target_id and await work.accounts.get(target_id) else None
                await work.audit.append(actor_id, valid_target, action, AUDIT_RESULT_MAP["denied"], {"reason": failure.code})
            else:
                await work.audit.append(actor_id, target_id, action, AUDIT_RESULT_MAP["succeeded"], details)
        if error is not None:
            raise error
        return result

    async def checked_permissions(self, work, access, codes: tuple[str, ...]) -> tuple[str, ...]:
        codes = tuple(sorted(set(codes)))
        existing = {permission.code for permission in await work.access.permissions()}
        if not set(codes).issubset(existing):
            raise ResourceNotFound("Permission not found")
        if not set(codes).issubset(access.permissions):
            raise DelegationDenied()
        return codes


class ListRolesUseCase(AdminUseCase):
    async def execute(self, access_token: str):
        async def operation(work, access):
            return await work.access.roles(), {}
        return await self.perform(access_token, PERMISSION_MAP["roles_read"].code, AUDIT_ACTION_MAP["roles_list"], operation)


class ListPermissionsUseCase(AdminUseCase):
    async def execute(self, access_token: str):
        async def operation(work, access):
            return await work.access.permissions(), {}
        return await self.perform(access_token, PERMISSION_MAP["permissions_read"].code, AUDIT_ACTION_MAP["permissions_list"], operation)


class CreateRoleUseCase(AdminUseCase):
    async def execute(self, access_token: str, code: str, description: str,
                      permissions: tuple[str, ...], is_assignable: bool = True):
        if re.fullmatch(r"[A-Z][A-Z0-9_]{1,63}", code) is None:
            raise InvalidInput("Role code must contain 2–64 uppercase ASCII letters, digits or underscores")
        description = description_value(description)
        async def operation(work, access):
            if await work.access.role_by_code(code) is not None:
                raise ResourceConflict("Role already exists")
            codes = await self.checked_permissions(work, access, permissions)
            role = await work.access.create_role(code, description, codes, is_assignable)
            return role, {"role_id": role.id, "permissions": list(codes)}
        return await self.perform(access_token, PERMISSION_MAP["roles_create"].code, AUDIT_ACTION_MAP["roles_create"], operation)


class UpdateRoleUseCase(AdminUseCase):
    async def execute(self, access_token: str, role_id: int, description: str,
                      permissions: tuple[str, ...], is_assignable: bool = True):
        description = description_value(description)
        async def operation(work, access):
            role = await work.access.role(role_id)
            if role is None:
                raise ResourceNotFound("Role not found")
            definition = ROLE_BY_CODE.get(role.code)
            if definition is not None and definition.protected:
                raise ProtectedRole(definition.code)
            # Editing a more privileged role is also a form of delegation.
            if not set(role.permissions).issubset(access.permissions):
                raise DelegationDenied()
            codes = await self.checked_permissions(work, access, permissions)
            result = await work.access.update_role(role_id, description, codes, is_assignable)
            return result, {"role_id": role_id, "permissions": list(codes), "is_assignable": is_assignable}
        return await self.perform(access_token, PERMISSION_MAP["roles_update"].code, AUDIT_ACTION_MAP["roles_update"], operation)


class CreatePermissionUseCase(AdminUseCase):
    async def execute(self, access_token: str, code: str, description: str):
        if re.fullmatch(r"[a-z][a-z0-9_.:-]{1,127}", code) is None:
            raise InvalidInput("Invalid permission code")
        description = description_value(description)
        async def operation(work, access):
            if code in {permission.code for permission in await work.access.permissions()}:
                raise ResourceConflict("Permission already exists")
            permission = await work.access.create_permission(code, description)
            role_ids = []
            for definition in ROLE_MAP.values():
                if definition.grants_all_permissions:
                    role = await work.access.role_by_code(definition.code)
                    if role is not None:
                        role_ids.append(role.id)
            await work.access.include_permission_in_roles(permission.id, tuple(role_ids))
            return permission, {"permission": code}
        return await self.perform(access_token, PERMISSION_MAP["permissions_manage"].code, AUDIT_ACTION_MAP["permissions_create"], operation)


class GrantRoleUseCase(AdminUseCase):
    granting = True

    async def execute(self, access_token: str, account_id: UUID, role_id: int) -> bool:
        operation_key = "roles_assign" if self.granting else "roles_revoke"
        permission = PERMISSION_MAP[operation_key].code
        async def operation(work, access):
            target = await work.accounts.get(account_id, lock=True)
            if target is None:
                raise ResourceNotFound("Account not found")
            require_active(target)
            if await work.credentials.by_account(account_id) is None:
                raise RegisteredAccountRequired()
            role = await work.access.role(role_id)
            if role is None:
                raise ResourceNotFound("Role not found")
            if not set(role.permissions).issubset(access.permissions):
                raise DelegationDenied()
            if self.granting:
                if not role.is_assignable:
                    raise RoleNotAssignable()
                changed = await work.access.grant(account_id, role_id, access.account_id)
            else:
                definition = ROLE_BY_CODE.get(role.code)
                if (definition is not None and definition.minimum_active_accounts > 0
                        and await work.access.has_role(account_id, role_id)
                        and await work.access.active_role_account_count(role_id, ACCOUNT_STATUS_MAP["active"])
                        <= definition.minimum_active_accounts):
                    raise LastAdministrator(definition.code)
                changed = await work.access.revoke(account_id, role_id, self.clock.now())
            return changed, {"role_id": role_id, "changed": changed}
        return await self.perform(access_token, permission, AUDIT_ACTION_MAP[operation_key], operation, target_id=account_id)


class RevokeRoleUseCase(GrantRoleUseCase):
    granting = False


class ListAuditUseCase(AdminUseCase):
    async def execute(self, access_token: str, limit: int = 50, offset: int = 0):
        if not 1 <= limit <= 100 or offset < 0:
            raise InvalidInput("Invalid audit pagination")
        async def operation(work, access):
            return await work.audit.list(limit, offset), {"limit": limit, "offset": offset}
        return await self.perform(access_token, PERMISSION_MAP["audit_read"].code, AUDIT_ACTION_MAP["audit_list"], operation)


class BootstrapAdminUseCase:
    def __init__(self, uow: UnitOfWorkFactory):
        self.uow = uow

    async def execute(self, login: str) -> UUID:
        login = normalize_login(login)
        async with self.uow() as work:
            await work.access.lock()
            credentials = await work.credentials.by_login(login)
            if credentials is None:
                raise ResourceNotFound("Register the administrator account first")
            require_active(await work.accounts.get(credentials.account_id, lock=True))
            role = (await seed_catalog(work))[BOOTSTRAP_ROLE_KEY]
            changed = await work.access.grant(credentials.account_id, role.id, None)
            await work.audit.append(None, credentials.account_id, AUDIT_ACTION_MAP["bootstrap_admin"], AUDIT_RESULT_MAP["succeeded"],
                                    {"role_id": role.id, "changed": changed})
        return credentials.account_id
