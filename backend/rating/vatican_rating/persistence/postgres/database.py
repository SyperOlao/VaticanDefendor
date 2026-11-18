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

    def __init__(self, url: str, pool_size: int = 10, *, lock_timeout_ms: int | None = None,
                 statement_timeout_ms: int | None = None, idle_transaction_timeout_ms: int | None = None):
        parsed_url = postgres_url(url)
        options = parsed_url.query.get("options", "") + " -c timezone=UTC"
        # Runtime API budgets are explicit; migrations keep their own timeout policy.
        for name, value in [('lock_timeout', lock_timeout_ms), ('statement_timeout', statement_timeout_ms),
                            ('idle_in_transaction_session_timeout', idle_transaction_timeout_ms)]:
            if value is not None:
                if type(value) is not int or value < 1:
                    raise ValueError(f'Invalid PostgreSQL {name}')
                options += f' -c {name}={value}'
        self.engine = create_engine(parsed_url, pool_pre_ping=True,
                                    pool_size=pool_size, max_overflow=0,
                                    pool_timeout=3, connect_args={"connect_timeout": 3, "options": options})
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
