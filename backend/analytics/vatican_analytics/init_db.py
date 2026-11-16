"""Create a fresh ClickHouse schema directly from ORM metadata."""
import os

from sqlalchemy.schema import CreateTable

from .persistence.clickhouse import Base, Database


def create_schema(connection) -> None:
    # ClickHouse DDL is not transactional. IF NOT EXISTS permits retry after a partial run.
    for table in Base.metadata.sorted_tables:
        connection.execute(CreateTable(table, if_not_exists=True))


def init_db(url: str | None = None) -> None:
    database = Database(url or os.environ["VD_ANALYTICS_DATABASE_URL"])
    try:
        with database.engine.connect() as connection:
            create_schema(connection)
    finally:
        database.close()


if __name__ == "__main__":
    init_db()
