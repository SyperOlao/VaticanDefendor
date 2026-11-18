from collections.abc import AsyncIterator

from dishka import AsyncContainer, Provider, Scope, from_context, make_async_container, provide

from ..application.access import (
    AuthorizeUseCase, BootstrapAdminUseCase, CreatePermissionUseCase, CreateRoleUseCase,
    GrantRoleUseCase, ListAuditUseCase, ListPermissionsUseCase, ListRolesUseCase,
    RevokeRoleUseCase, UpdateRoleUseCase,
)
from ..application.policy import AuthPolicy
from ..application.ports import Clock, PasswordService, TokenService, UnitOfWorkFactory
from ..application.services import AttemptLimiter, SessionIssuer
from ..application.use_cases import (
    AuthenticateUseCase, CreateGuestUseCase, LoginUseCase, LogoutUseCase,
    RecoverUseCase, RefreshUseCase, RegisterUseCase,
)
from ..persistence.postgres.database import Database
from ..persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory
from ..security.clock import SystemClock
from ..security.passwords import Argon2Passwords
from ..security.tokens import SecureTokens
from .settings import AuthSettings


class AuthProvider(Provider):
    settings = from_context(provides=AuthSettings, scope=Scope.APP)
    clock = provide(SystemClock, provides=Clock, scope=Scope.APP)
    tokens = provide(SecureTokens, provides=TokenService, scope=Scope.APP)
    uow = provide(PostgresUnitOfWorkFactory, provides=UnitOfWorkFactory, scope=Scope.APP)

    @provide(scope=Scope.APP)
    def policy(self, settings: AuthSettings) -> AuthPolicy:
        return settings.policy

    @provide(scope=Scope.APP)
    async def database(self, settings: AuthSettings) -> AsyncIterator[Database]:
        database = Database(settings)
        try:
            yield database
        finally:
            await database.close()

    @provide(scope=Scope.APP)
    async def passwords(self, policy: AuthPolicy) -> PasswordService:
        service = Argon2Passwords(policy)
        await service.initialize()
        return service

    authorize = provide(AuthorizeUseCase, scope=Scope.REQUEST)
    bootstrap_admin = provide(BootstrapAdminUseCase, scope=Scope.REQUEST)
    list_roles = provide(ListRolesUseCase, scope=Scope.REQUEST)
    create_role = provide(CreateRoleUseCase, scope=Scope.REQUEST)
    update_role = provide(UpdateRoleUseCase, scope=Scope.REQUEST)
    list_permissions = provide(ListPermissionsUseCase, scope=Scope.REQUEST)
    create_permission = provide(CreatePermissionUseCase, scope=Scope.REQUEST)
    grant_role = provide(GrantRoleUseCase, scope=Scope.REQUEST)
    revoke_role = provide(RevokeRoleUseCase, scope=Scope.REQUEST)
    list_audit = provide(ListAuditUseCase, scope=Scope.REQUEST)
    limiter = provide(AttemptLimiter, scope=Scope.REQUEST)
    issuer = provide(SessionIssuer, scope=Scope.REQUEST)
    create_guest = provide(CreateGuestUseCase, scope=Scope.REQUEST)
    register = provide(RegisterUseCase, scope=Scope.REQUEST)
    login = provide(LoginUseCase, scope=Scope.REQUEST)
    authenticate = provide(AuthenticateUseCase, scope=Scope.REQUEST)
    refresh = provide(RefreshUseCase, scope=Scope.REQUEST)
    logout = provide(LogoutUseCase, scope=Scope.REQUEST)
    recover = provide(RecoverUseCase, scope=Scope.REQUEST)


def create_container(*providers: Provider) -> AsyncContainer:
    return make_async_container(*providers)


def create_auth_container(settings: AuthSettings, *providers: Provider) -> AsyncContainer:
    return make_async_container(AuthProvider(), *providers, context={AuthSettings: settings})
