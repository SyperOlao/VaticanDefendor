from collections.abc import Iterator
import httpx
from dishka import Provider, Scope, from_context, make_container, provide
from .settings import Settings
from ..persistence.postgres.database import Database
from ..persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory
from ..application.ports import UnitOfWorkFactory, DefinitionsGateway, AuthGateway
from ..application.use_cases import WalletUseCases
from ..application.health import HealthUseCases
from ..application.broker import BrokerOperations
from ..application.analytics import DeliverAnalytics
from ..application.ports import AnalyticsGateway
from ..integrations.analytics import HttpAnalyticsGateway
from ..migrate import SCHEMA_REVISION
from ..integrations.http import AuthHttpGateway, DefinitionsHttpGateway
from ..integrations.auth_grpc import AuthGrpcGateway


class ServiceProvider(Provider):
    settings = from_context(provides=Settings, scope=Scope.APP)

    @provide(scope=Scope.APP)
    def client(self) -> Iterator[httpx.Client]:
        with httpx.Client(timeout=httpx.Timeout(5, connect=2), trust_env=False, follow_redirects=False) as client:
            yield client

    @provide(scope=Scope.APP)
    def database(self, settings: Settings) -> Iterator[Database]:
        database = Database(settings.database_url)
        try:
            yield database
        finally:
            database.close()

    @provide(scope=Scope.APP)
    def uow(self, database: Database) -> UnitOfWorkFactory:
        return PostgresUnitOfWorkFactory(database)

    @provide(scope=Scope.APP)
    def auth(self, client: httpx.Client, settings: Settings) -> Iterator[AuthGateway]:
        if settings.auth_transport == 'grpc':
            gateway = AuthGrpcGateway(settings)
            try:
                yield gateway
            finally:
                gateway.close()
        else:
            yield AuthHttpGateway(client, settings.auth_url)

    @provide(scope=Scope.APP)
    def health(self, uow: UnitOfWorkFactory) -> HealthUseCases:
        return HealthUseCases(uow, SCHEMA_REVISION)

    use_case = provide(WalletUseCases, scope=Scope.APP)
    broker_operations = provide(BrokerOperations, scope=Scope.APP)
    analytics_delivery = provide(DeliverAnalytics, scope=Scope.APP)

    @provide(scope=Scope.APP)
    def analytics(self, client: httpx.Client, settings: Settings) -> AnalyticsGateway:
        return HttpAnalyticsGateway(client, settings.analytics_url, settings.service_secret)


def create_container(settings, *providers):
    return make_container(ServiceProvider(), *providers, context={Settings: settings})
