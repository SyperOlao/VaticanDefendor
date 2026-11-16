from alembic import context
from sqlalchemy import create_engine, pool
from sqlalchemy.engine import make_url

from vatican_auth.persistence.postgres import Base
from vatican_auth.bootstrap.settings import AuthSettings

config = context.config


def run(connection):
    context.configure(connection=connection, target_metadata=Base.metadata,
                      compare_type=True, compare_server_default=True)
    with context.begin_transaction():
        context.run_migrations()


if context.is_offline_mode():
    url = make_url(AuthSettings.from_environment().database_url).set(drivername="postgresql+psycopg")
    context.configure(url=url, target_metadata=Base.metadata, literal_binds=True,
                      dialect_opts={"paramstyle": "named"})
    with context.begin_transaction():
        context.run_migrations()
elif config.attributes.get("connection") is not None:
    run(config.attributes["connection"])
else:
    url = make_url(AuthSettings.from_environment().database_url).set(drivername="postgresql+psycopg")
    engine = create_engine(url, poolclass=pool.NullPool)
    try:
        with engine.begin() as connection:
            connection.exec_driver_sql("SELECT pg_advisory_xact_lock(691750362)")
            run(connection)
    finally:
        engine.dispose()
