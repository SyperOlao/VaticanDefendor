import os

from alembic import context
from sqlalchemy import create_engine, pool

from vatican_analytics.persistence.postgres import Base
from vatican_analytics.persistence.postgres.database import postgres_url, environment_url

config = context.config


def run(connection):
    context.configure(connection=connection, target_metadata=Base.metadata,
                      compare_type=True, compare_server_default=True)
    with context.begin_transaction():
        context.run_migrations()


if context.is_offline_mode():
    context.configure(url=postgres_url(environment_url("VD_ANALYTICS_INBOX")), target_metadata=Base.metadata,
                      literal_binds=True, dialect_opts={"paramstyle": "named"})
    with context.begin_transaction():
        context.run_migrations()
elif config.attributes.get("connection") is not None:
    run(config.attributes["connection"])
else:
    engine = create_engine(postgres_url(environment_url("VD_ANALYTICS_INBOX")), poolclass=pool.NullPool)
    try:
        with engine.begin() as connection:
            connection.exec_driver_sql("SELECT pg_advisory_xact_lock(681750361)")
            run(connection)
    finally:
        engine.dispose()
