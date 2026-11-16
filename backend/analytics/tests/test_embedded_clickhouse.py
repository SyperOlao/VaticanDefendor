"""Optional native ClickHouse engine verification without a Docker daemon."""
import json
from uuid import uuid4

import pytest


def test_embedded_clickhouse_orm_initialization_and_deduplication(tmp_path):
    pytest.importorskip("chdb", reason="Install chdb for the optional embedded engine check")
    from chdb.session import Session
    from clickhouse_sqlalchemy.drivers.http.base import ClickHouseDialect_http
    from vatican_analytics.persistence.clickhouse import Base
    from vatican_analytics.init_db import create_schema

    session = Session(str(tmp_path / "clickhouse"))

    class EmbeddedConnection:
        def execute(self, statement):
            session.query(str(statement.compile(dialect=ClickHouseDialect_http())))

    connection = EmbeddedConnection()
    try:
        create_schema(connection)
        event_id, player_id = uuid4(), uuid4()
        session.query(f"INSERT INTO raw_game_events (event_id,player_id,event_name,source,schema_version,occurred_at,payload_json) "
                      f"VALUES ('{event_id}','{player_id}','session_started','unity',1,now64(3),'{{}}')")
        create_schema(connection)
        tables = {
            json.loads(line)["name"] for line in str(session.query("SHOW TABLES", "JSONEachRow")).splitlines()
        }
        assert tables == set(Base.metadata.tables)
        for table in Base.metadata.sorted_tables:
            columns = {row["name"]: row["type"] for row in map(json.loads, str(session.query(
                f"DESCRIBE TABLE {table.name}", "JSONEachRow")).splitlines())}
            expected = {column.name: column.type.compile(dialect=ClickHouseDialect_http()) for column in table.c}
            assert columns == expected
        for _ in range(2):
            session.query(f"INSERT INTO raw_game_events (event_id,player_id,event_name,source,schema_version,occurred_at,payload_json) "
                          f"VALUES ('{event_id}','{player_id}','session_started','unity',1,now64(3),'{{}}')")
        assert str(session.query(f"SELECT count() FROM raw_game_events FINAL WHERE event_id='{event_id}'", "CSV")).strip() == "1"
        assert str(session.query(f"SELECT isNull(app_version) FROM raw_game_events FINAL WHERE event_id='{event_id}'", "CSV")).strip() == "1"
    finally:
        session.close()
