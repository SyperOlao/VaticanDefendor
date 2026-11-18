from dataclasses import dataclass, field
from datetime import datetime
from uuid import UUID


@dataclass(frozen=True)
class AccountRecord:
    id: UUID
    status: str


@dataclass(frozen=True)
class CredentialsRecord:
    account_id: UUID
    login: str
    password_hash: str = field(repr=False)
    recovery_hash: bytes = field(repr=False)


@dataclass(frozen=True)
class GuestRecord:
    account_id: UUID
    expires_at: datetime
    account_status: str


@dataclass(frozen=True)
class SessionRecord:
    id: UUID
    account_id: UUID
    access_expires_at: datetime
    refresh_expires_at: datetime
    revoked_at: datetime | None
    account_status: str


@dataclass(frozen=True)
class AttemptRecord:
    attempts: int
    window_start: datetime


@dataclass(frozen=True)
class Principal:
    account_id: UUID
    session_id: UUID | None
    is_guest: bool


@dataclass(frozen=True)
class GuestTokens:
    account_id: UUID
    token: str = field(repr=False)
    expires_at: datetime


@dataclass(frozen=True)
class SessionTokens:
    account_id: UUID
    session_id: UUID
    access_token: str = field(repr=False)
    refresh_token: str = field(repr=False)
    access_expires_at: datetime
    refresh_expires_at: datetime


@dataclass(frozen=True)
class CredentialResult:
    session: SessionTokens
    recovery_code: str = field(repr=False)


@dataclass(frozen=True)
class PasswordCheck:
    valid: bool
    replacement_hash: str | None = field(default=None, repr=False)


@dataclass(frozen=True)
class RoleRecord:
    id: int
    code: str
    description: str
    is_assignable: bool
    permissions: tuple[str, ...]


@dataclass(frozen=True)
class PermissionRecord:
    id: int
    code: str
    description: str


@dataclass(frozen=True)
class AccessSnapshot:
    account_id: UUID
    session_id: UUID | None
    is_guest: bool
    roles: tuple[str, ...]
    permissions: tuple[str, ...]


@dataclass(frozen=True)
class AuditRecord:
    id: UUID
    actor_account_id: UUID | None
    target_account_id: UUID | None
    action: str
    result: str
    details: dict
    created_at: datetime
