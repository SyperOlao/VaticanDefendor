"""Report validation, service authentication, and failures without any databases."""
from datetime import date

from dishka import Provider, Scope, provide
from fastapi.testclient import TestClient
import pytest

from vatican_analytics.application.errors import ApplicationError
from vatican_analytics.application.reports import DailyReportsGateway, ReadDailyReport
from vatican_analytics.bootstrap.di import create_container
from vatican_analytics.bootstrap.settings import Settings
from vatican_analytics.persistence.clickhouse.repositories.reports import ClickHouseDailyReports
from vatican_analytics.presentation import app as app_module


def totals():
    return {
        'runs': dict(completed=3, players=2, victories=2, defeats=1, heroKills=12,
                     towerKills=15, buildingUpgrades=9, knownXpAwarded=80, runsWithXp=2, runsWithoutXp=1),
        'levelUps': dict(transitions=1, players=1, levelsGained=2),
        'levelGifts': dict(claims=1, players=1),
        'cases': dict(openings=2, players=2, guaranteedOpenings=1, paidFromInventory=1, paidFromWallet=1),
    }


class Reports:
    def __init__(self):
        self.calls = []
        self.unavailable = False

    def daily(self, day):
        self.calls.append(day)
        if self.unavailable:
            raise ApplicationError(503, 'Analytics report is temporarily unavailable')
        return totals()


@pytest.mark.parametrize('day', ['20261117', '2026-11-17T00:00:00Z', '2026-11-31',
                                 '1969-12-31', '2201-01-01', '', None, True,
                                 "2026-11-17'; DROP TABLE raw_game_events; --"])
def test_invalid_day_is_rejected_before_warehouse_access(day):
    gateway = Reports()
    with pytest.raises(ApplicationError) as error:
        ReadDailyReport(gateway).execute(day)
    assert error.value.status_code == 422
    assert gateway.calls == []


@pytest.mark.parametrize('day', ['1970-01-01', '2024-02-29', '2026-11-17', '2200-12-31'])
def test_canonical_utc_day_and_missing_xp_are_explicit(day):
    gateway = Reports()
    reply = ReadDailyReport(gateway).execute(day)
    assert gateway.calls == [date.fromisoformat(day)]
    assert reply == {'day': day, 'timezone': 'UTC', **totals()}
    assert reply['runs']['runsWithoutXp'] == 1


def test_driver_errors_do_not_expose_credentials_or_fabricate_zero_totals():
    class BrokenEngine:
        def connect(self):
            raise RuntimeError('password=private SQL SELECT secret')

    class Warehouse:
        engine = BrokenEngine()

    with pytest.raises(ApplicationError) as error:
        ClickHouseDailyReports(Warehouse()).daily(date(2026, 11, 17))
    assert error.value.status_code == 503
    assert error.value.detail == 'Analytics report is temporarily unavailable'
    assert 'private' not in str(error.value)


def test_internal_http_report_auth_validation_and_outage_without_inbox_database(monkeypatch, caplog):
    gateway = Reports()

    class Overrides(Provider):
        @provide(scope=Scope.APP, override=True)
        def reports(self) -> DailyReportsGateway:
            return gateway

    monkeypatch.setattr(app_module, 'create_container',
                        lambda settings: create_container(settings, Overrides()))
    settings = Settings('postgresql://localhost:1/unused', 's' * 40,
                        'clickhouse+http://localhost:1/unused')
    with TestClient(app_module.create_app(settings)) as client:
        path = '/internal/reports/daily'
        headers = {'X-Service-Secret': settings.service_secret}
        assert client.get(path, params={'day': '2026-11-17'}).status_code == 401
        assert client.get(path, params={'day': '2026-11-17'},
                          headers={'X-Service-Secret': 'incorrect'}).status_code == 401
        assert gateway.calls == []
        for params in ({}, {'day': '2026-02-30'}, {'day': '20261117'}):
            assert client.get(path, params=params, headers=headers).status_code == 422
        assert gateway.calls == []
        response = client.get(path, params={'day': '2026-11-17'}, headers=headers)
        assert response.status_code == 200
        assert response.json() == {'day': '2026-11-17', 'timezone': 'UTC', **totals()}
        assert response.headers['cache-control'] == 'no-store'
        gateway.unavailable = True
        outage = client.get(path, params={'day': '2026-11-17'}, headers=headers)
        assert outage.status_code == 503 and 'runs' not in outage.json()
        assert any(record.name == 'vatican_analytics.errors' and 'Analytics request failed' in record.message
                   for record in caplog.records)
        # The inbox readiness endpoint remains a separate concern.
        assert 'DailyReport' in client.get('/openapi.json').json()['components']['schemas']


def test_cli_prints_json_without_service_secret_or_inbox_settings(monkeypatch, capsys):
    import json
    from vatican_analytics.cli import daily_report

    closed = []

    class Warehouse:
        def __init__(self, url):
            assert url == 'clickhouse+http://localhost:8123/reports_test'

        def close(self):
            closed.append(True)

    monkeypatch.setenv('VD_ANALYTICS_DATABASE_URL', 'clickhouse+http://localhost:8123/reports_test')
    monkeypatch.delenv('VD_SERVICE_SECRET', raising=False)
    monkeypatch.delenv('VD_ANALYTICS_INBOX_DATABASE_URL', raising=False)
    monkeypatch.setattr(daily_report, 'Database', Warehouse)
    monkeypatch.setattr(daily_report, 'ClickHouseDailyReports', lambda db: Reports())
    monkeypatch.setattr('sys.argv', ['daily_report', '2026-11-17'])
    daily_report.main()
    assert json.loads(capsys.readouterr().out) == {'day': '2026-11-17', 'timezone': 'UTC', **totals()}
    assert closed == [True]
