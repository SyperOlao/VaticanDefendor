from collections.abc import Iterator
from dishka import Provider, Scope, from_context, make_container, provide
from .settings import Settings
from ..persistence.postgres.database import Database
from ..persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory
from ..application.ports import AuthGateway, UnitOfWorkFactory
from ..application.access import AuthenticatePlayer
from ..application.broker import BrokerOperations
from ..application.use_cases import RatingUseCases
from ..application.health import HealthUseCases
from ..migrate import SCHEMA_REVISION
from ..integrations.auth_grpc import AuthGrpcGateway


class ServiceProvider(Provider):
    settings = from_context(provides=Settings, scope=Scope.APP)

    @provide(scope=Scope.APP)
    def database(self, settings: Settings) -> Iterator[Database]:
        database = Database(settings.database_url, settings.pool_size,
                            lock_timeout_ms=settings.lock_timeout_ms,
                            statement_timeout_ms=settings.statement_timeout_ms,
                            idle_transaction_timeout_ms=max(15000, settings.statement_timeout_ms * 2))
        try:
            yield database
        finally:
            database.close()

    @provide(scope=Scope.APP)
    def uow(self, database: Database) -> UnitOfWorkFactory:
        return PostgresUnitOfWorkFactory(database)

    @provide(scope=Scope.APP)
    def auth(self, settings: Settings) -> Iterator[AuthGateway]:
        gateway = AuthGrpcGateway(settings)
        try:
            yield gateway
        finally:
            gateway.close()

    @provide(scope=Scope.APP)
    def health(self, uow: UnitOfWorkFactory) -> HealthUseCases:
        return HealthUseCases(uow, SCHEMA_REVISION)

    use_case = provide(RatingUseCases, scope=Scope.APP)
    access = provide(AuthenticatePlayer, scope=Scope.APP)
    broker_operations = provide(BrokerOperations, scope=Scope.APP)


def create_container(settings, *providers, auth_gateway=None):
    if auth_gateway is not None:
        override = Provider()
        override.provide(lambda: auth_gateway, provides=AuthGateway, scope=Scope.APP)
        providers = (*providers, override)
    return make_container(ServiceProvider(), *providers, context={Settings: settings})
