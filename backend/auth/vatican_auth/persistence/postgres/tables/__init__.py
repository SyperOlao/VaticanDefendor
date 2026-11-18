"""Auth-owned tables. Every foreign key stays inside this service's metadata."""
from .accounts import Account
from .access import AccountRole, AdminAuditLog, Permission, Role, RolePermission
from .attempts import AuthAttempt, OAuthLoginAttempt
from .credentials import AccountLogin
from .identities import AuthIdentity
from .sessions import AuthSession, GuestCredential

__all__ = [
    "Account", "AccountLogin", "AccountRole", "AdminAuditLog", "AuthAttempt",
    "AuthIdentity", "AuthSession", "GuestCredential", "OAuthLoginAttempt",
    "Permission", "Role", "RolePermission",
]
