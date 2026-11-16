from sqlalchemy.engine import make_url
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker, create_async_engine

from ...bootstrap.settings import AuthSettings


class Database:
    def __init__(self, settings: AuthSettings):
        url = make_url(settings.database_url)
        if url.get_backend_name() != "postgresql":
            raise ValueError("Auth requires its own PostgreSQL database")
        url = url.set(drivername="postgresql+psycopg")
        options = str(url.query.get("options", "")) + " -c timezone=UTC"
        self.engine = create_async_engine(
            url, hide_parameters=True, pool_pre_ping=True, pool_size=settings.pool_size, max_overflow=0,
            pool_timeout=3, connect_args={"connect_timeout": 3, "options": options},
        )
        self.sessions = async_sessionmaker(self.engine, class_=AsyncSession, expire_on_commit=False)

    async def close(self) -> None:
        await self.engine.dispose()
