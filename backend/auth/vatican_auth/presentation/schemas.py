from datetime import datetime
from typing import Literal
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field, SecretStr
from pydantic.alias_generators import to_camel

from ..application.models import CredentialResult, SessionTokens
from .examples import (
    ACCESS, ACCESS_TOKEN, ACCOUNT_ID, AUDIT, CREDENTIAL, GUEST, GUEST_TOKEN, LOGIN,
    PERMISSION, PERMISSION_CREATE, RECOVERY_CODE, REFRESH_TOKEN, REQUEST_ID, ROLE,
    ROLE_CREATE, ROLE_UPDATE, SESSION, SESSION_ID,
)


class Model(BaseModel):
    model_config = ConfigDict(extra="forbid", alias_generator=to_camel, populate_by_name=True, from_attributes=True)


class CredentialsRequest(Model):
    model_config = ConfigDict(json_schema_extra={"examples": [LOGIN]})
    login: str = Field(min_length=3, max_length=64, pattern=r"^[A-Za-z0-9_.-]+$",
                       description="Логин без пробелов: латинские буквы, цифры, точка, подчёркивание и дефис. Регистр не учитывается.", examples=[LOGIN["login"]])
    password: SecretStr = Field(min_length=1, max_length=1024,
                                description="Пароль. Для регистрации и восстановления действуют ограничения, указанные в начале документации; для входа используйте существующий пароль.",
                                examples=[LOGIN["password"]])


class RegisterRequest(CredentialsRequest):
    model_config = ConfigDict(json_schema_extra={"examples": [LOGIN]})
    guest_token: SecretStr | None = Field(default=None, description="Необязательно. Токен из POST /v1/guests для сохранения UUID гостя. Поле можно полностью убрать при обычной регистрации.", examples=[GUEST_TOKEN])


class RefreshRequest(Model):
    model_config = ConfigDict(json_schema_extra={"examples": [{"refreshToken": REFRESH_TOKEN}]})
    refresh_token: SecretStr = Field(min_length=43, max_length=43, description="Текущий одноразовый refreshToken из ответа регистрации, входа или предыдущего refresh. Это не accessToken.", examples=[REFRESH_TOKEN])


class RecoverRequest(CredentialsRequest):
    model_config = ConfigDict(json_schema_extra={"examples": [{"login": LOGIN["login"], "password": "NewExamplePass123!", "recoveryCode": RECOVERY_CODE}]})
    recovery_code: SecretStr = Field(min_length=43, max_length=43, description="Действующий recoveryCode из регистрации или последнего восстановления. После успешного восстановления этот код заменяется новым.", examples=[RECOVERY_CODE])


class LogoutRequest(Model):
    model_config = ConfigDict(json_schema_extra={"examples": [{"allSessions": False}, {"allSessions": True}]})
    all_sessions: bool = Field(default=False, description="false — выйти только из текущей сессии; true — отозвать все сессии и гостевые токены этого аккаунта.", examples=[False, True])


class GuestResponse(Model):
    model_config = ConfigDict(json_schema_extra={"examples": [GUEST]})
    account_id: UUID = Field(description="UUID аккаунта. Сохраняется при привязке логина к гостю; используется другими сервисами как идентификатор игрока.", examples=[ACCOUNT_ID])
    token: str = Field(repr=False, description="Гостевой токен. Передавайте как Bearer для /me и /logout либо в guestToken при регистрации.", examples=[GUEST_TOKEN])
    expires_at: datetime = Field(description="Срок действия гостевого токена, UTC.", examples=[GUEST["expiresAt"]])


class SessionResponse(Model):
    model_config = ConfigDict(json_schema_extra={"examples": [SESSION]})
    account_id: UUID = Field(description="UUID аккаунта. Сохраняется при привязке логина к гостю; используется другими сервисами как идентификатор игрока.", examples=[ACCOUNT_ID])
    session_id: UUID = Field(description="UUID текущей сессии. Меняется после обновления токенов.", examples=[SESSION_ID])
    access_token: str = Field(repr=False, description="Токен доступа. Вставьте это значение в Authorize без префикса Bearer.", examples=[ACCESS_TOKEN])
    refresh_token: str = Field(repr=False, description="Одноразовый токен для POST /v1/auth/refresh. Храните отдельно от accessToken; не используйте для Authorize.", examples=[REFRESH_TOKEN])
    access_expires_at: datetime = Field(description="Окончание срока accessToken в UTC.", examples=[SESSION["accessExpiresAt"]])
    refresh_expires_at: datetime = Field(description="Окончание срока refreshToken в UTC.", examples=[SESSION["refreshExpiresAt"]])
    token_type: Literal["Bearer"] = Field(default="Bearer", description="Схема заголовка Authorization: Bearer <accessToken>.")

    @classmethod
    def from_session(cls, value: SessionTokens):
        return cls.model_validate(value)


class CredentialResponse(SessionResponse):
    model_config = ConfigDict(json_schema_extra={"examples": [CREDENTIAL]})
    recovery_code: str = Field(repr=False, description="Код восстановления. Сохраните: повторно получить его через /me или /login нельзя. При восстановлении выдаётся новый код.", examples=[RECOVERY_CODE])

    @classmethod
    def from_credentials(cls, value: CredentialResult):
        return cls(**SessionResponse.from_session(value.session).model_dump(), recovery_code=value.recovery_code)


class AccessResponse(Model):
    model_config = ConfigDict(json_schema_extra={"examples": [ACCESS]})
    account_id: UUID = Field(description="UUID аккаунта. Сохраняется при привязке логина к гостю; используется другими сервисами как идентификатор игрока.", examples=[ACCOUNT_ID])
    session_id: UUID | None = Field(description="UUID сессии; null для гостевого токена.", examples=[SESSION_ID, None])
    is_guest: bool = Field(description="true, если запрос выполнен с гостевым токеном.", examples=[False])
    roles: list[str] = Field(description="Коды действующих ролей аккаунта. Новый обычный аккаунт не имеет административных ролей.", examples=[["MODERATOR"]])
    permissions: list[str] = Field(description="Эффективные коды разрешений из активных ролей; проверяются сервером при каждом защищённом действии.", examples=[ROLE_CREATE["permissions"]])


class RoleCreateRequest(Model):
    model_config = ConfigDict(json_schema_extra={"examples": [ROLE_CREATE]})
    code: str = Field(min_length=2, max_length=64, pattern=r"^[A-Z][A-Z0-9_]+$", description="Уникальный код роли: заглавные латинские буквы, цифры и подчёркивание. После создания не меняется.", examples=["MODERATOR"])
    description: str = Field(min_length=1, max_length=512, description="Понятное человеку описание назначения роли или разрешения.", examples=["Просмотр ролей и разрешений"])
    permissions: list[str] = Field(max_length=1000, description="Полный набор существующих кодов разрешений, а не их числовых ID. Коды можно получить через GET /v1/admin/permissions.", examples=[ROLE_CREATE["permissions"]])
    is_assignable: bool = Field(default=True, description="Разрешены ли новые назначения роли. false не отзывает уже существующие назначения.", examples=[True])


class RoleUpdateRequest(Model):
    model_config = ConfigDict(json_schema_extra={"examples": [ROLE_UPDATE]})
    description: str = Field(min_length=1, max_length=512, description="Понятное человеку описание назначения роли или разрешения.", examples=["Просмотр ролей и разрешений"])
    permissions: list[str] = Field(max_length=1000, description="Полный набор существующих кодов разрешений, а не их числовых ID. Коды можно получить через GET /v1/admin/permissions.", examples=[ROLE_CREATE["permissions"]])
    is_assignable: bool = Field(default=True, description="Разрешены ли новые назначения роли. false не отзывает уже существующие назначения.", examples=[True])


class PermissionCreateRequest(Model):
    model_config = ConfigDict(json_schema_extra={"examples": [PERMISSION_CREATE]})
    code: str = Field(min_length=2, max_length=128, pattern=r"^[a-z][a-z0-9_.:-]+$", description="Уникальный код разрешения для проверки в бизнес-операциях. Допустимы строчные латинские буквы, цифры, точка, подчёркивание, дефис и двоеточие.", examples=[PERMISSION_CREATE["code"]])
    description: str = Field(min_length=1, max_length=512, description="Понятное человеку описание назначения роли или разрешения.", examples=["Просмотр ролей и разрешений"])


class RoleResponse(RoleCreateRequest):
    model_config = ConfigDict(json_schema_extra={"examples": [ROLE]})
    id: int = Field(description="Числовой ID роли для путей изменения и назначения. Используйте ID из реального ответа.", examples=[2])


class PermissionResponse(PermissionCreateRequest):
    model_config = ConfigDict(json_schema_extra={"examples": [PERMISSION]})
    id: int = Field(description="Числовой ID записи каталога. В permissions роли передаётся code, а не этот ID.", examples=[9])


class ChangedResponse(Model):
    changed: bool = Field(description="true — назначение изменилось; false — оно уже было в нужном состоянии. Повтор операции не создаёт дубликаты.", examples=[True, False])


class AuditResponse(Model):
    model_config = ConfigDict(json_schema_extra={"examples": [AUDIT]})
    id: UUID = Field(description="UUID записи административного аудита.", examples=[AUDIT["id"]])
    actor_account_id: UUID | None = Field(description="Кто выполнил действие; null для операторского bootstrap или неизвестного аккаунта.", examples=[ACCOUNT_ID])
    target_account_id: UUID | None = Field(description="Аккаунт, к которому относится действие; null, если оно касается каталога или цель не найдена.", examples=[ACCOUNT_ID, None])
    action: str = Field(description="Код административного действия.", examples=[AUDIT["action"]])
    result: str = Field(description="Результат: успешное действие или отказ.", examples=[AUDIT["result"]])
    details: dict = Field(description="Дополнительные сведения: роль, изменённые права или причина отказа. Секреты сюда не записываются.", examples=[AUDIT["details"]])
    created_at: datetime = Field(description="Время записи аудита, UTC.", examples=[AUDIT["createdAt"]])


class ErrorIssue(Model):
    field: str = Field(description="Поле, которое нужно исправить, например body.login или password.", examples=["body.login"])
    type: str = Field(description="Машинный код ошибки поля.", examples=["missing"])
    message: str = Field(description="Безопасное пояснение для пользователя без исходного значения поля.", examples=["Required field is missing"])


class ErrorResponse(Model):
    code: str = Field(description="Машинный код ошибки для обработки клиентом.", examples=["InvalidCredentials"])
    detail: str = Field(description="Безопасное пояснение ошибки; внутренние исключения и SQL не возвращаются.", examples=["Invalid or expired credential"])
    request_id: str = Field(description="Идентификатор запроса для диагностики; совпадает с X-Request-ID в заголовке ответа.", examples=[REQUEST_ID])
    errors: list[ErrorIssue] = Field(default_factory=list, description="Список ошибок полей. Для ошибок доступа, конфликтов и сбоев сервера обычно пустой.")


class Liveness(Model):
    status: Literal['live'] = Field(description='HTTP-процесс запущен.')


class Readiness(Model):
    status: Literal['ready'] = Field(description='Собственная БД auth доступна и схема актуальна.')
    schemaRevision: str = Field(description='Текущая ревизия схемы auth.')
