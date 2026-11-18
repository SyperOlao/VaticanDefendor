import os
import secrets
from uuid import UUID, uuid4

import psycopg
from psycopg import sql
from psycopg.conninfo import make_conninfo
import pytest
from fastapi.testclient import TestClient

from .support import game_app
from vatican_backend.persistence.postgres.tables import Profile
from vatican_backend.application.energy import ConfigureEnergy
from vatican_backend.domain.energy import EnergyRules
from vatican_backend.application.progression import ConfigureProgression
from vatican_backend.domain.progression import ProgressionRules, XPRule, LevelDefinition
from vatican_backend.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory
from vatican_backend.migrate import migrate
from vatican_backend.persistence.postgres import Database
from vatican_backend.bootstrap.settings import RewardPolicy, ServerSlot, Settings


@pytest.fixture()
def storage():
    url = os.environ.get("VD_TEST_DATABASE_URL")
    if not url:
        pytest.skip("Set VD_TEST_DATABASE_URL to a disposable PostgreSQL database")
    schema = "orm_" + uuid4().hex
    with psycopg.connect(url) as connection:
        connection.execute(sql.SQL("CREATE SCHEMA {}").format(sql.Identifier(schema)))
    scoped_url = make_conninfo(url, options="-c search_path=" + schema)
    database = Database(scoped_url)
    try:
        migrate(scoped_url)
        yield database, scoped_url
    finally:
        database.close()
        with psycopg.connect(url) as connection:
            connection.execute(sql.SQL("DROP SCHEMA {} CASCADE").format(sql.Identifier(schema)))


@pytest.fixture()
def backend(storage):
    _, url = storage
    slot = ServerSlot(uuid4(), "127.0.0.1", 7979, secrets.token_urlsafe(32))
    other = ServerSlot(uuid4(), "127.0.0.1", 7980, secrets.token_urlsafe(32))
    settings = Settings(url, {slot.server_id: slot, other.server_id: other}, 120,
                        {"sha256-test-release": RewardPolicy(25)}, energy_ruleset_id="test-energy-v1")
    app = game_app(settings)
    with app.state.database.transaction() as session:
        session.add(Profile(account_id=UUID(app.state.test_player["accountId"]), version=1, display_name="Player"))
    ConfigureEnergy(PostgresUnitOfWorkFactory(app.state.database)).execute(EnergyRules("test-energy-v1", 100, 60_000, 1, 5))
    ConfigureProgression(PostgresUnitOfWorkFactory(app.state.database)).execute(ProgressionRules(
        "sha256-test-release", (XPRule("RUN_VICTORY", "*", 0, 0),), (LevelDefinition(1, 0),)))
    with TestClient(app) as client:
        yield client, settings, slot
