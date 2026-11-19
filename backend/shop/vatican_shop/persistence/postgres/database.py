from contextlib import contextmanager
from collections.abc import Iterator

from psycopg.conninfo import conninfo_to_dict
from sqlalchemy import URL, create_engine
from sqlalchemy.engine import make_url
from sqlalchemy.orm import Session, sessionmaker


def postgres_url(value: str) -> URL:
    """Accept existing libpq DSNs as well as SQLAlchemy and PostgreSQL URLs."""
    if value.startswith("postgresql+psycopg://"):
        return make_url(value)
    parameters = conninfo_to_dict(value)
    port = parameters.pop("port", None)
    return URL.create(
        "postgresql+psycopg", username=parameters.pop("user", None),
        password=parameters.pop("password", None), host=parameters.pop("host", None),
        port=int(port) if port else None, database=parameters.pop("dbname", None),
        query=parameters,
    )


class Database:
    """One engine per application, one transaction and Session per use case."""

    def __init__(self, url: str, pool_size: int = 10):
        parsed_url = postgres_url(url)
        options = parsed_url.query.get("options", "") + " -c timezone=UTC"
        # A half-open connection must fail while the worker still has time to
        # return to its durable queue. Preserve explicit settings in the DSN.
        network_limits = {"connect_timeout": 5, "keepalives_idle": 10, "keepalives_interval": 3,
                          "keepalives_count": 3, "tcp_user_timeout": 8000}
        self.engine = create_engine(parsed_url, pool_pre_ping=True,
                                    pool_size=pool_size, max_overflow=0,
                                    pool_timeout=3,
                                    connect_args={**{key: value for key, value in network_limits.items()
                                                     if key not in parsed_url.query}, "options": options})
        self.sessions = sessionmaker(self.engine, expire_on_commit=False)

    @contextmanager
    def transaction(self) -> Iterator[Session]:
        with self.sessions.begin() as session:
            yield session

    def close(self) -> None:
        self.engine.dispose()


def environment_url(prefix="VD"):
    import os
    from psycopg.conninfo import make_conninfo
    explicit = os.getenv(prefix + "_DATABASE_URL")
    if explicit:
        return explicit
    return make_conninfo(host=os.environ[prefix + "_DB_HOST"], port=os.getenv(prefix + "_DB_PORT", "5432"),
                         dbname=os.environ[prefix + "_DB_NAME"], user=os.environ[prefix + "_DB_USER"],
                         password=os.environ[prefix + "_DB_PASSWORD"])
