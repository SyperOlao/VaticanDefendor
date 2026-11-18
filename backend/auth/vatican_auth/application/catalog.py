"""Shared definitions for built-in access policy; custom roles still live in PostgreSQL."""
from dataclasses import dataclass
from types import MappingProxyType


@dataclass(frozen=True)
class PermissionDefinition:
    code: str
    description: str


@dataclass(frozen=True)
class RoleDefinition:
    code: str
    description: str
    is_assignable: bool = True
    protected: bool = False
    grants_all_permissions: bool = False
    minimum_active_accounts: int = 0
    permissions: tuple[str, ...] = ()


PERMISSION_MAP = MappingProxyType({
    "roles_read": PermissionDefinition("auth.roles.read", "Read roles"),
    "roles_create": PermissionDefinition("auth.roles.create", "Create roles"),
    "roles_update": PermissionDefinition("auth.roles.update", "Change role definitions"),
    "roles_assign": PermissionDefinition("auth.roles.assign", "Assign roles to accounts"),
    "roles_revoke": PermissionDefinition("auth.roles.revoke", "Revoke account roles"),
    "permissions_read": PermissionDefinition("auth.permissions.read", "Read permissions"),
    "permissions_manage": PermissionDefinition("auth.permissions.manage", "Create permission definitions"),
    "audit_read": PermissionDefinition("auth.audit.read", "Read administrative audit"),
})

ROLE_MAP = MappingProxyType({
    "admin": RoleDefinition(
        code="ADMIN", description="Auth administrator", protected=True,
        grants_all_permissions=True, minimum_active_accounts=1,
    ),
})
ROLE_BY_CODE = MappingProxyType({definition.code: definition for definition in ROLE_MAP.values()})
BOOTSTRAP_ROLE_KEY = "admin"

ACCOUNT_STATUS_MAP = MappingProxyType({"active": "ACTIVE", "banned": "BANNED"})
AUDIT_RESULT_MAP = MappingProxyType({"denied": "DENIED", "succeeded": "SUCCEEDED"})
AUDIT_ACTION_MAP = MappingProxyType({
    "access_check": "auth.access_check", "roles_list": "auth.roles.list",
    "roles_create": PERMISSION_MAP["roles_create"].code, "roles_update": PERMISSION_MAP["roles_update"].code,
    "roles_assign": PERMISSION_MAP["roles_assign"].code, "roles_revoke": PERMISSION_MAP["roles_revoke"].code,
    "permissions_list": "auth.permissions.list", "permissions_create": "auth.permissions.create",
    "audit_list": "auth.audit.list", "bootstrap_admin": "auth.bootstrap_admin",
})
