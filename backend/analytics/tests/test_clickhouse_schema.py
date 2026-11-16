"""DDL and service-boundary checks; live ClickHouse checks are opt-in."""
import os
from pathlib import Path
import subprocess
import sys
from uuid import uuid4

import pytest

from vatican_analytics.init_db import init_db
from vatican_analytics.persistence.clickhouse import Database


def test_analytics_runs_without_game_backend_or_postgres_driver():
    source = """
import sys
from vatican_analytics.persistence.clickhouse import Base
assert not any(name.startswith(('vatican_backend', 'psycopg')) for name in sys.modules)
assert not any(table.foreign_keys for table in Base.metadata.tables.values())
"""
    subprocess.run([sys.executable, "-c", source], cwd=Path(__file__).parents[1],
                   env={**os.environ, "PYTHONPATH": str(Path(__file__).parents[1])}, check=True)


def test_postgres_url_cannot_be_used_for_analytics():
    with pytest.raises(ValueError):
        Database("postgresql://localhost/game")


def test_live_clickhouse_initialization_and_event_replay():
    url = os.environ.get("VD_TEST_ANALYTICS_DATABASE_URL")
    if not url:
        pytest.skip("Set VD_TEST_ANALYTICS_DATABASE_URL to a disposable ClickHouse database")
    init_db(url)
    init_db(url)
    database = Database(url)
    event_id, player_id = uuid4(), uuid4()
    try:
        with database.engine.connect() as connection:
            for _ in range(2):
                connection.exec_driver_sql(
                    "INSERT INTO raw_game_events (event_id,player_id,event_name,source,schema_version,occurred_at,payload_json) "
                    "VALUES (%(event)s,%(player)s,'session_started','unity',1,now64(3),'{}')",
                    {"event": str(event_id), "player": str(player_id)})
            assert connection.exec_driver_sql("SELECT count() FROM raw_game_events FINAL WHERE event_id=%(event)s",
                                              {"event": str(event_id)}).scalar() == 1
    finally:
        database.close()
