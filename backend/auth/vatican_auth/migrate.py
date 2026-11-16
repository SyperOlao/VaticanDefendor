from pathlib import Path

from alembic import command
from alembic.config import Config
from sqlalchemy import create_engine
from sqlalchemy.engine import make_url
from sqlalchemy.pool import NullPool

from .bootstrap.settings import AuthSettings

SCHEMA_REVISION = "0001_auth_initial"


def migration_config() -> Config:
    root = Path(__file__).resolve().parent.parent
    config = Config(str(root / "alembic.ini"))
    config.set_main_option("script_location", str(root / "alembic"))
    return config


def migrate(url: str | None = None) -> None:
    parsed = make_url(url or AuthSettings.from_environment().database_url)
    if parsed.get_backend_name() != "postgresql":
        raise ValueError("Auth requires PostgreSQL")
    engine = create_engine(parsed.set(drivername="postgresql+psycopg"), poolclass=NullPool)
    try:
        with engine.begin() as connection:
            connection.exec_driver_sql("SELECT pg_advisory_xact_lock(691750362)")
            config = migration_config()
            config.attributes["connection"] = connection
            command.upgrade(config, "head")
    finally:
        engine.dispose()


if __name__ == "__main__":
    migrate()
