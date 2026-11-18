from collections.abc import Iterator
from dishka import Provider, Scope, from_context, make_container, provide
from .settings import Settings
from ..application.ports import UnitOfWorkFactory, ProjectionGateway
from ..application.use_cases import AnalyticsUseCases, ProjectEvents
from ..application.health import HealthUseCases
from ..persistence.postgres.database import Database
from ..persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory
from ..persistence.clickhouse.database import Database as Warehouse
from ..persistence.clickhouse.repositories.projection import ClickHouseProjection
from ..migrate import SCHEMA_REVISION


class ServiceProvider(Provider):
    settings=from_context(provides=Settings,scope=Scope.APP)
    @provide(scope=Scope.APP)
    def database(self,settings: Settings) -> Iterator[Database]:
        database=Database(settings.database_url)
        try:yield database
        finally:database.close()
    @provide(scope=Scope.APP)
    def uow(self,database: Database) -> UnitOfWorkFactory:
        return PostgresUnitOfWorkFactory(database)
    @provide(scope=Scope.APP)
    def projection(self,settings: Settings) -> Iterator[ProjectionGateway]:
        database=Warehouse(settings.clickhouse_url)
        try:yield ClickHouseProjection(database)
        finally:database.close()
    @provide(scope=Scope.APP)
    def health(self,uow: UnitOfWorkFactory) -> HealthUseCases:
        return HealthUseCases(uow,SCHEMA_REVISION)
    use_case=provide(AnalyticsUseCases,scope=Scope.APP)
    worker=provide(ProjectEvents,scope=Scope.APP)


def create_container(settings,*providers):
    from .reports import ReportsProvider
    return make_container(ServiceProvider(),ReportsProvider(),*providers,context={Settings:settings})
