"""Execute actual report SQL over isolated, disposable ClickHouse databases."""
from datetime import date, datetime, timezone
import os
from uuid import uuid4

from fastapi.testclient import TestClient
import pytest
from sqlalchemy.engine import make_url

from vatican_analytics.application.reports import ReadDailyReport
from vatican_analytics.bootstrap.settings import Settings
from vatican_analytics.init_db import create_schema
from vatican_analytics.persistence.clickhouse import Base, Database
from vatican_analytics.persistence.clickhouse.repositories.projection import ClickHouseProjection
from vatican_analytics.persistence.clickhouse.repositories.reports import ClickHouseDailyReports
from vatican_analytics.presentation.app import create_app
from .test_business_events import case_payload, gift_payload
from .test_ingestion import payload
from .test_daily_reports import totals


@pytest.fixture
def warehouse():
    url = os.getenv('VD_TEST_ANALYTICS_DATABASE_URL')
    if not url:
        pytest.skip('Set VD_TEST_ANALYTICS_DATABASE_URL to disposable ClickHouse with CREATE DATABASE permission')
    admin = Database(url)
    name = 'daily_report_test_' + uuid4().hex
    database = None
    try:
        with admin.engine.connect() as connection:
            connection.exec_driver_sql('CREATE DATABASE ' + name)
        database = Database(make_url(url).set(database=name).render_as_string(hide_password=False))
        with database.engine.connect() as connection:
            create_schema(connection)
        yield database
    finally:
        if database is not None:
            database.close()
        with admin.engine.connect() as connection:
            connection.exec_driver_sql('DROP DATABASE IF EXISTS ' + name + ' SYNC')
        admin.close()


def test_real_daily_report_deduplicates_bounds_unknown_xp_and_late_deliveries(warehouse):
    project = ClickHouseProjection(warehouse)
    report = ReadDailyReport(ClickHouseDailyReports(warehouse))
    first_player, second_player = str(uuid4()), str(uuid4())
    run = payload(accountId=first_player, completedAt='2026-11-17T00:00:00+00:00')
    legacy = payload(accountId=first_player, completedAt='2026-11-17T23:59:59.999+00:00',
                     outcome='defeat', progression=None)
    zero = payload(accountId=second_player, completedAt='2026-11-17T12:00:00+00:00',
                   progression={'xpGranted': 0, 'totalXp': 9000, 'previousLevel': 3, 'currentLevel': 3,
                                'breakdown': dict(heroKillXp=0, towerKillXp=0, tycoonXp=0, victoryXp=0)})
    gift = {**gift_payload(), 'accountId': first_player, 'claimedAt': '2026-11-17T00:00:00Z'}
    inventory_case = {**case_payload(), 'accountId': first_player, 'completedAt': '2026-11-17T10:00:00Z'}
    wallet_case = {**case_payload(), 'accountId': second_player, 'paymentMode': 'WALLET',
                   'guaranteedTriggered': False, 'pityBefore': 0, 'pityAfter': 1,
                   'completedAt': '2026-11-17T23:59:59.999Z'}
    for body in (run, legacy, zero, gift, inventory_case, wallet_case):
        # Physical redelivery is normal until background merges occur.
        project.project(body)
        project.project(body)
    for at in ('2026-11-16T23:59:59.999Z', '2026-11-18T00:00:00Z'):
        project.project(payload(completedAt=at))
        project.project({**gift_payload(), 'claimedAt': at})
        project.project({**case_payload(), 'completedAt': at})

    assert report.execute('2026-11-17') == {'day': '2026-11-17', 'timezone': 'UTC', **totals()}
    assert report.execute('2026-11-17')['runs']['knownXpAwarded'] == 80
    # Late arrival is incorporated by the next read of the original day.
    project.project(payload(accountId=first_player, completedAt='2026-11-17T05:00:00Z'))
    later = report.execute('2026-11-17')
    assert later['runs']['completed'] == 4 and later['runs']['knownXpAwarded'] == 160
    assert later['levelUps']['levelsGained'] == 4
    # Reports neither persist stale aggregates nor write additional events.
    with warehouse.engine.connect() as connection:
        assert connection.exec_driver_sql('SELECT count() FROM agg_player_daily').scalar() == 0
        assert connection.exec_driver_sql('SELECT count() FROM agg_player_xp_daily').scalar() == 0


def test_empty_day_returns_exact_zero_counts(warehouse):
    report = ClickHouseDailyReports(warehouse).daily(date(2026, 11, 17))
    assert all(value == 0 for section in report.values() for value in section.values())


def test_live_http_report_uses_warehouse_without_opening_postgres(warehouse):
    settings = Settings('postgresql://localhost:1/unused', 's' * 40,
                        warehouse.engine.url.render_as_string(hide_password=False))
    with TestClient(create_app(settings)) as client:
        response = client.get('/internal/reports/daily', params={'day': '2026-11-17'},
                              headers={'X-Service-Secret': settings.service_secret})
        assert response.status_code == 200
        assert response.json()['day'] == '2026-11-17'
        assert all(value == 0 for key, section in response.json().items()
                   if key not in ('day', 'timezone') for value in section.values())


def test_large_valid_xp_totals_do_not_wrap_uint64_or_sum_lifetime_xp(warehouse):
    project = ClickHouseProjection(warehouse)
    maximum = 9_223_372_036_854_775_807
    for _ in range(3):
        project.project(payload(completedAt='2026-11-17T00:00:00Z', progression={
            'xpGranted': maximum, 'totalXp': maximum, 'previousLevel': 1, 'currentLevel': 1,
            'breakdown': dict(heroKillXp=maximum, towerKillXp=0, tycoonXp=0, victoryXp=0)}))
    report = ClickHouseDailyReports(warehouse).daily(date(2026, 11, 17))
    assert report['runs']['knownXpAwarded'] == maximum * 3 > 2**64 - 1
    assert report['runs']['runsWithXp'] == 3 and report['runs']['runsWithoutXp'] == 0


def test_unconfirmed_and_undated_legacy_gifts_are_not_successful_daily_claims(warehouse):
    with warehouse.engine.connect() as connection:
        table = Base.metadata.tables['fact_level_gift_claims']
        for status, at in [('PENDING', datetime(2026, 11, 17, 10, tzinfo=timezone.utc)), ('CLAIMED', None)]:
            connection.execute(table.insert(), [{'claim_id': str(uuid4()), 'player_id': str(uuid4()),
                'rewarded_level': 2, 'reward_bundle_id': str(uuid4()), 'claim_status': status,
                'claimed_at': at}])
    result = ClickHouseDailyReports(warehouse).daily(date(2026, 11, 17))
    assert result['levelGifts'] == {'claims': 0, 'players': 0}
