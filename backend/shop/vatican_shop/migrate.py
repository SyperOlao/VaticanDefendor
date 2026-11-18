import os
from pathlib import Path

from alembic import command
from alembic.config import Config

from .persistence.postgres.database import Database, environment_url


SCHEMA_REVISION = "0001_shop_initial"


def migration_config() -> Config:
    directory = Path(__file__).resolve().parent.parent
    config = Config(str(directory / "alembic.ini"))
    config.set_main_option("script_location", str(directory / "alembic"))
    return config


def migrate(database_url: str | None = None) -> None:
    database = Database(database_url or environment_url("VD_SHOP"))
    try:
        with database.engine.begin() as connection:
            connection.exec_driver_sql("SELECT pg_advisory_xact_lock(681750361)")
            config = migration_config()
            config.attributes["connection"] = connection
            command.upgrade(config, "head")
    finally:
        database.close()


if __name__ == "__main__":
    migrate()
