"""Create ClickHouse tables and apply additive, repeatable schema updates."""
from sqlalchemy import text
from sqlalchemy.schema import CreateTable

from .persistence.clickhouse import Base, Database
from .persistence.clickhouse.database import environment_url


def create_schema(connection) -> None:
    # ClickHouse DDL is not transactional. IF NOT EXISTS permits retry after a partial run.
    for table in Base.metadata.sorted_tables:
        connection.execute(CreateTable(table, if_not_exists=True))
    # CREATE IF NOT EXISTS leaves old tables untouched. Historical facts are dedicated.
    connection.execute(text("ALTER TABLE fact_run_settlements ADD COLUMN IF NOT EXISTS "
        "simulation_mode LowCardinality(String) DEFAULT 'DEDICATED'"))


def init_db(url: str | None = None) -> None:
    selected=url or environment_url()
    database = Database(selected)
    try:
        with database.engine.connect() as connection:
            create_schema(connection)
    finally:
        database.close()


if __name__ == "__main__":
    init_db()
