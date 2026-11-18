"""Use real HTTP adapters and APIs with isolated PostgreSQL and ClickHouse stores."""
from contextlib import ExitStack
from datetime import datetime, timedelta, timezone
import json
import os
from time import sleep
from uuid import uuid4

from dishka import Provider, Scope, provide
from fastapi.testclient import TestClient
import httpx
import pytest
from sqlalchemy import select
from sqlalchemy.engine import make_url

from vatican_analytics.application.use_cases import ProjectEvents
from vatican_analytics.bootstrap.settings import Settings as AnalyticsSettings
from vatican_analytics.init_db import create_schema
from vatican_analytics.persistence.clickhouse import Database as Warehouse
from vatican_analytics.persistence.clickhouse.repositories.projection import ClickHouseProjection
from vatican_analytics.presentation.app import create_app as analytics_app
from vatican_backend.application.messaging import DeliverRewards
from vatican_backend.application.errors import ApplicationError as GameError
from vatican_backend.integrations.http import HttpRewardDelivery
from vatican_backend.persistence.postgres.tables import OutboxMessage as GameMessage
from vatican_backend.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as GameUow
from vatican_inventory.application.ports import DefinitionsGateway
from vatican_inventory.bootstrap.settings import Settings as InventorySettings
from vatican_inventory.presentation.app import create_app as inventory_app
from vatican_rating.bootstrap.settings import Settings as RatingSettings
from vatican_rating.presentation.app import create_app as rating_app
from vatican_shop.application.analytics import DeliverAnalytics
from vatican_shop.application.errors import ApplicationError as ShopError
from vatican_shop.application.use_cases import ShopUseCases, SettlePurchases
from vatican_shop.bootstrap.settings import Settings as ShopSettings
from vatican_shop.integrations.analytics import HttpAnalyticsGateway
from vatican_shop.integrations.shop import PurchaseGateway
from vatican_shop.persistence.postgres.tables import OutboxMessage as ShopMessage
from vatican_shop.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as ShopUow
from vatican_wallet.bootstrap.settings import Settings as WalletSettings
from vatican_wallet.presentation.app import create_app as wallet_app
from .test_settlement import world, complete, Catalog, Definitions


SECRET = 'compatibility-service-secret-123456789'


@pytest.mark.parametrize('status', [202, 401, 403, 408, 429, 500, 503])
def test_game_and_shop_retry_the_same_transient_analytics_http_failures(status):
    with httpx.Client(transport=httpx.MockTransport(lambda request: httpx.Response(status))) as client:
        message = {'id': uuid4(), 'destination': 'analytics', 'event_name': 'run.analytics',
                   'schema_version': 1, 'payload': {}}
        with pytest.raises(GameError) as game:
            HttpRewardDelivery(client, {'analytics': 'http://analytics'}, SECRET).deliver(message)
        with pytest.raises(ShopError) as shop:
            HttpAnalyticsGateway(client, 'http://analytics', SECRET).deliver(message)
        assert game.value.status_code == shop.value.status_code == 503


@pytest.fixture
def warehouse():
    url = os.getenv('VD_TEST_ANALYTICS_DATABASE_URL')
    if not url:
        pytest.skip('Set VD_TEST_ANALYTICS_DATABASE_URL for cross-service warehouse checks')
    admin = Warehouse(url)
    name = 'service_compatibility_' + uuid4().hex
    database = None
    try:
        with admin.engine.connect() as connection:
            connection.exec_driver_sql('CREATE DATABASE ' + name)
        database = Warehouse(make_url(url).set(database=name).render_as_string(hide_password=False))
        with database.engine.connect() as connection:
            create_schema(connection)
        yield database
    finally:
        if database is not None:
            database.close()
        with admin.engine.connect() as connection:
            connection.exec_driver_sql('DROP DATABASE IF EXISTS ' + name + ' SYNC')
        admin.close()


def database_url(database):
    return database.engine.url.render_as_string(hide_password=False)


def drain(worker):
    for _ in range(20):
        if not worker.once():
            return
    pytest.fail('Worker did not finish the available deliveries')


def ready(database, table):
    with database.transaction() as session:
        for row in session.scalars(select(table).where(table.published_at.is_(None))):
            row.available_at = datetime.now(timezone.utc) - timedelta(seconds=1)


def daily_report(client, day, headers):
    # A cold warehouse can exceed the report's three-second query budget.
    # Follow the API's retry contract, without accepting partial or incorrect data.
    for attempt in range(3):
        response = client.get('/internal/reports/daily', params={'day': day}, headers=headers)
        if response.status_code != 503 or attempt == 2:
            return response
        assert response.headers['Retry-After'] == '1'
        sleep(1)


@pytest.mark.parametrize('live_warehouse', [False, True], ids=['http-contracts', 'live-clickhouse'])
def test_run_and_purchase_http_receipts_projection_and_daily_report_are_compatible(world, request, live_warehouse):
    w = world
    warehouse = request.getfixturevalue('warehouse') if live_warehouse else None
    warehouse_url = database_url(warehouse) if warehouse is not None else 'clickhouse+http://127.0.0.1:1/unavailable'

    class Content(Provider):
        @provide(scope=Scope.APP, provides=DefinitionsGateway)
        def definitions(self):
            return Definitions()

    with ExitStack() as stack:
        clients = {
            'wallet': stack.enter_context(TestClient(wallet_app(WalletSettings(database_url(w.wallet_db), SECRET)))),
            'inventory': stack.enter_context(TestClient(inventory_app(
                InventorySettings(database_url(w.inventory_db), SECRET), providers=(Content(),)))),
            'rating': stack.enter_context(TestClient(rating_app(RatingSettings(database_url(w.rating.uow.database), SECRET)))),
            'analytics': stack.enter_context(TestClient(analytics_app(AnalyticsSettings(
                database_url(w.analytics.database), SECRET, warehouse_url)))),
        }
        lose_purchase_ack = [False]

        def dispatch(request):
            assert request.headers['X-Service-Secret'] == SECRET
            assert w.game_db.engine.pool.checkedout() == w.shop_db.engine.pool.checkedout() == 0
            response = clients[request.url.host].request(request.method, request.url.path,
                headers={'X-Service-Secret': request.headers['X-Service-Secret']}, json=json.loads(request.content))
            if (lose_purchase_ack[0] and request.url.host == 'analytics' and response.status_code == 200):
                lose_purchase_ack[0] = False
                raise httpx.ReadTimeout('Injected loss after the receiver committed', request=request)
            return httpx.Response(response.status_code, json=response.json())

        transport = stack.enter_context(httpx.Client(transport=httpx.MockTransport(dispatch)))
        game_delivery = DeliverRewards(GameUow(w.game_db), HttpRewardDelivery(transport,
            {name: 'http://' + name for name in clients}, SECRET))
        projector = ProjectEvents(w.analytics.uow, ClickHouseProjection(warehouse)) if warehouse is not None else w.analytics.worker
        result, _ = complete(w)
        drain(game_delivery)
        state = w.runs.assignment_status(result.run_id, w.account_id)
        assert state['rewardStatus'] == state['ratingStatus'] == 'COMPLETED'
        assert state['analyticsStatus'] == 'PENDING'  # HTTP 202 cannot acknowledge projection.
        assert w.wallet.balance(w.account_id)['balance'] == 525
        assert len(w.inventory.get(w.account_id)['Weapons']) == 2
        assert projector.once() and not projector.once()
        ready(w.game_db, GameMessage)
        drain(game_delivery)
        assert w.runs.assignment_status(result.run_id, w.account_id)['analyticsStatus'] == 'COMPLETED'

        with w.game_db.transaction() as session:
            source = session.scalar(select(GameMessage).where(GameMessage.destination == 'analytics'))
            day = datetime.fromisoformat(source.payload['completedAt']).date().isoformat()
        headers = {'X-Service-Secret': SECRET}
        daily = daily_report(clients['analytics'], day, headers) if live_warehouse else clients['analytics'].get(
            '/internal/reports/daily', params={'day': day}, headers=headers)
        if live_warehouse:
            assert daily.status_code == 200
            assert daily.json()['runs']['completed'] == daily.json()['runs']['victories'] == 1
            assert daily.json()['runs']['runsWithXp'] == 1 and daily.json()['runs']['knownXpAwarded'] == 0
        else:
            # A report-store outage does not change confirmed business deliveries.
            assert daily.status_code == 503 and 'runs' not in daily.json()
            assert daily.headers['Retry-After'] == '1'

        shop_uow = ShopUow(w.shop_db)
        shop = ShopUseCases(shop_uow, Catalog())
        settings = ShopSettings(database_url(w.shop_db), SECRET,
                                wallet_url='http://wallet', inventory_url='http://inventory')
        purchases = SettlePurchases(shop_uow, PurchaseGateway(transport, settings))
        analytics = DeliverAnalytics(shop_uow, HttpAnalyticsGateway(transport, 'http://analytics', SECRET))
        operation = uuid4()
        shop.purchase(w.account_id, operation, 'sword', 'test-v1')
        drain(purchases)
        assert shop.receipt(w.account_id, operation)['status'] == 'Completed'
        assert analytics.once()  # PENDING/202 from the actual analytics route.
        assert shop.receipt(w.account_id, operation)['analyticsStatus'] == 'PENDING'
        assert projector.once() and not projector.once()
        ready(w.shop_db, ShopMessage)
        lose_purchase_ack[0] = True
        assert analytics.once()  # The applied response is lost without changing the business outcome.
        assert shop.receipt(w.account_id, operation)['analyticsStatus'] == 'PENDING'
        ready(w.shop_db, ShopMessage)
        assert analytics.once() and not analytics.once()
        assert shop.receipt(w.account_id, operation)['analyticsStatus'] == 'COMPLETED'
        assert w.wallet.balance(w.account_id)['balance'] == 500
        assert len(w.inventory.get(w.account_id)['Weapons']) == 3
        assert not projector.once()

        # The extra purchase fact is separate from run totals and real-money revenue.
        if live_warehouse:
            assert daily_report(clients['analytics'], day, headers).json() == daily.json()
            with warehouse.engine.connect() as connection:
                assert connection.exec_driver_sql('SELECT count(),sum(charged_amount) FROM fact_shop_purchases FINAL').one() == (1, 25)
                assert connection.exec_driver_sql('SELECT count() FROM fact_purchases FINAL').scalar() == 0
                assert connection.exec_driver_sql('SELECT count() FROM fact_run_settlements FINAL').scalar() == 1
        else:
            facts = w.analytics.projection.tables
            assert len(facts['fact_shop_purchases']) == len(facts['fact_run_settlements']) == 1
            assert sum(row['charged_amount'] for row in facts['fact_shop_purchases'].values()) == 25
            assert 'fact_purchases' not in facts
        assert w.runs.apply_result(w.slot.server_id, w.slot.secret, result)['replayed']
        drain(game_delivery)
        assert w.wallet.balance(w.account_id)['balance'] == 500
        assert len(w.inventory.get(w.account_id)['Weapons']) == 3
