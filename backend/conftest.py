"""Isolated PostgreSQL schemas for service tests; no production tables are modified."""
import importlib
import os
from pathlib import Path
import sys
from uuid import uuid4

import psycopg
from psycopg import sql
from psycopg.conninfo import make_conninfo
import pytest

for name in ('auth', 'game_backend', 'wallet', 'inventory', 'shop', 'rating', 'analytics'):
    sys.path.insert(0, str(Path(__file__).parent / name))


@pytest.fixture
def service_storage_factory():
    url = os.environ.get('VD_TEST_DATABASE_URL')
    if not url:
        pytest.skip('Set VD_TEST_DATABASE_URL to a disposable PostgreSQL database')
    created = []

    def create(name):
        package = 'vatican_backend' if name == 'game_backend' else 'vatican_' + name
        schema = 'service_test_' + uuid4().hex
        with psycopg.connect(url) as connection:
            connection.execute(sql.SQL('CREATE SCHEMA {}').format(sql.Identifier(schema)))
        scoped = make_conninfo(url, options='-c search_path=' + schema)
        module = importlib.import_module(package + '.migrate')
        database = importlib.import_module(package + '.persistence.postgres').Database(scoped)
        created.append((schema, database))
        module.migrate(scoped)
        return database, scoped

    yield create
    for schema, database in reversed(created):
        database.close()
        with psycopg.connect(url) as connection:
            connection.execute(sql.SQL('DROP SCHEMA {} CASCADE').format(sql.Identifier(schema)))


@pytest.fixture
def wallet_storage(service_storage_factory):
    return service_storage_factory('wallet')


@pytest.fixture
def inventory_storage(service_storage_factory):
    return service_storage_factory('inventory')


@pytest.fixture
def shop_storage(service_storage_factory):
    return service_storage_factory('shop')


@pytest.fixture
def rating_storage(service_storage_factory):
    return service_storage_factory('rating')


@pytest.fixture
def analytics_storage(service_storage_factory):
    return service_storage_factory('analytics')


@pytest.fixture
def analytics_actor(analytics_storage):
    from contextlib import contextmanager
    from threading import local
    from vatican_analytics.application.use_cases import AnalyticsUseCases, ProjectEvents
    from vatican_analytics.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory
    from vatican_analytics.domain.projection import rows
    database=analytics_storage[0]
    active=local()
    transaction=database.transaction
    @contextmanager
    def tracked_transaction():
        active.depth=getattr(active,'depth',0)+1
        try:
            with transaction() as session:yield session
        finally:active.depth-=1
    database.transaction=tracked_transaction
    class Projection:
        def __init__(self):self.tables={};self.calls=0
        def project(self,payload):
            assert getattr(active,'depth',0)==0
            self.calls+=1
            for name,values in rows(payload).items():
                table=self.tables.setdefault(name,{})
                for row in values:
                    identity=next(row[k] for k in ('event_id','run_id','claim_id','opening_id','purchase_id') if k in row)
                    table[identity]=row
    class Actor:
        def __init__(self):
            self.database=database;self.projection=Projection()
            self.uow=PostgresUnitOfWorkFactory(database)
            self.api=AnalyticsUseCases(self.uow);self.worker=ProjectEvents(self.uow,self.projection)
        def receive(self,*args):
            reply=self.api.receive(*args)
            if reply['status']=='PENDING':
                self.worker.once()
                reply=self.api.receive(*args)
            return reply
    return Actor()
