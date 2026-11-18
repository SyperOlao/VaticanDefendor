from copy import deepcopy
from contextlib import contextmanager
from threading import local
from types import SimpleNamespace
from uuid import uuid4
import os

import pytest
import psycopg
from psycopg import sql
from psycopg.conninfo import make_conninfo

from vatican_inventory.application.errors import ApplicationError
from vatican_inventory.application.use_cases import InventoryUseCases
from vatican_inventory.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory
from vatican_inventory.persistence.postgres.database import Database
from vatican_inventory.migrate import migrate


@pytest.fixture
def service_storage_factory():
    """Allow the service's documented test command to run independently of backend/conftest.py."""
    url = os.getenv("VD_TEST_DATABASE_URL")
    if not url:
        pytest.skip("Set VD_TEST_DATABASE_URL to an isolated PostgreSQL database")
    created = []
    def create(name):
        if name != "inventory":
            raise ValueError("Inventory tests must only create their own schema")
        schema = "inventory_test_" + uuid4().hex
        with psycopg.connect(url) as connection:
            connection.execute(sql.SQL("CREATE SCHEMA {}").format(sql.Identifier(schema)))
        scoped = make_conninfo(url, options="-c search_path=" + schema)
        database = Database(scoped)
        created.append((schema, database))
        migrate(scoped)
        return database, scoped
    yield create
    for schema, database in reversed(created):
        database.close()
        with psycopg.connect(url) as connection:
            connection.execute(sql.SQL("DROP SCHEMA {} CASCADE").format(sql.Identifier(schema)))


@pytest.fixture
def inventory_storage(service_storage_factory):
    return service_storage_factory("inventory")


DEFINITIONS = [
    {"Id": "sword.common", "ExperiencePerLevel": 100, "MaximumLevel": 20},
    {"Id": "axe.rare", "ExperiencePerLevel": 50, "MaximumLevel": 10},
]


@pytest.fixture
def inventory(inventory_storage):
    database, url = inventory_storage
    active = local()
    transaction = database.transaction

    @contextmanager
    def tracked_transaction():
        active.depth = getattr(active, "depth", 0) + 1
        try:
            with transaction() as session:
                yield session
        finally:
            active.depth -= 1

    database.transaction = tracked_transaction

    class Definitions:
        down = False
        values = deepcopy(DEFINITIONS)

        def get(self):
            assert getattr(active, "depth", 0) == 0, "Content HTTP must be outside the inventory transaction"
            if self.down:
                raise ApplicationError(503, "Content unavailable")
            return deepcopy(self.values)

    definitions = Definitions()
    return SimpleNamespace(database=database, url=url, definitions=definitions, account=uuid4(), active=active,
                           api=InventoryUseCases(PostgresUnitOfWorkFactory(database), definitions))
