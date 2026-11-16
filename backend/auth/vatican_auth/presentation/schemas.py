from datetime import datetime
from typing import Literal
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field, SecretStr
from pydantic.alias_generators import to_camel

from ..application.models import CredentialResult, SessionTokens


class Model(BaseModel):
    model_config = ConfigDict(extra="forbid", alias_generator=to_camel, populate_by_name=True, from_attributes=True)


class CredentialsRequest(Model):
    login: str = Field(min_length=3, max_length=64, pattern=r"^[A-Za-z0-9_.-]+$")
    password: SecretStr = Field(min_length=1, max_length=1024)


class RegisterRequest(CredentialsRequest):
    guest_token: SecretStr | None = None


class RefreshRequest(Model):
    refresh_token: SecretStr = Field(min_length=43, max_length=43)


class RecoverRequest(CredentialsRequest):
    recovery_code: SecretStr = Field(min_length=43, max_length=43)


class LogoutRequest(Model):
    all_sessions: bool = False


class GuestResponse(Model):
    account_id: UUID
    token: str = Field(repr=False)
    expires_at: datetime


class SessionResponse(Model):
    account_id: UUID
    session_id: UUID
    access_token: str = Field(repr=False)
    refresh_token: str = Field(repr=False)
    access_expires_at: datetime
    refresh_expires_at: datetime
    token_type: Literal["Bearer"] = "Bearer"

    @classmethod
    def from_session(cls, value: SessionTokens):
        return cls.model_validate(value)


class CredentialResponse(SessionResponse):
    recovery_code: str = Field(repr=False)

    @classmethod
    def from_credentials(cls, value: CredentialResult):
        return cls(**SessionResponse.from_session(value.session).model_dump(), recovery_code=value.recovery_code)


class AccessResponse(Model):
    account_id: UUID
    session_id: UUID | None
    is_guest: bool
    roles: list[str]
    permissions: list[str]


class RoleCreateRequest(Model):
    code: str = Field(min_length=2, max_length=64, pattern=r"^[A-Z][A-Z0-9_]+$")
    description: str = Field(min_length=1, max_length=512)
    permissions: list[str] = Field(max_length=1000)
    is_assignable: bool = True


class RoleUpdateRequest(Model):
    description: str = Field(min_length=1, max_length=512)
    permissions: list[str] = Field(max_length=1000)
    is_assignable: bool = True


class PermissionCreateRequest(Model):
    code: str = Field(min_length=2, max_length=128, pattern=r"^[a-z][a-z0-9_.:-]+$")
    description: str = Field(min_length=1, max_length=512)


class RoleResponse(RoleCreateRequest):
    id: int


class PermissionResponse(PermissionCreateRequest):
    id: int


class ChangedResponse(Model):
    changed: bool


class AuditResponse(Model):
    id: UUID
    actor_account_id: UUID | None
    target_account_id: UUID | None
    action: str
    result: str
    details: dict
    created_at: datetime


class ErrorIssue(Model):
    field: str
    type: str
    message: str


class ErrorResponse(Model):
    code: str
    detail: str
    request_id: str
    errors: list[ErrorIssue] = Field(default_factory=list)
