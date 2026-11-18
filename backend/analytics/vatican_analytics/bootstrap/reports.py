"""Separate report wiring keeps warehouse reads independent of PostgreSQL."""
from collections.abc import Iterator

from dishka import Provider, Scope, provide

from .settings import Settings
from ..application.reports import DailyReportsGateway, ReadDailyReport
from ..persistence.clickhouse.database import Database
from ..persistence.clickhouse.repositories.reports import ClickHouseDailyReports


class ReportsProvider(Provider):
    @provide(scope=Scope.APP)
    def reports(self, settings: Settings) -> Iterator[DailyReportsGateway]:
        database = Database(settings.clickhouse_url)
        try:
            yield ClickHouseDailyReports(database)
        finally:
            database.close()

    read_report = provide(ReadDailyReport, scope=Scope.APP)
