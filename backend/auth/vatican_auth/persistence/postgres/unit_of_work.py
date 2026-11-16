from contextlib import asynccontextmanager

from .database import Database
from .repositories.access import PostgresAccessRepository
from .repositories.audit import PostgresAuditRepository
from .repositories.accounts import PostgresAccountsRepository
from .repositories.attempts import PostgresAttemptsRepository
from .repositories.credentials import PostgresCredentialsRepository
from .repositories.guests import PostgresGuestsRepository
from .repositories.sessions import PostgresSessionsRepository


class PostgresUnitOfWork:
    def __init__(self, database: Database):
        self.database = database

    async def __aenter__(self):
        self._session = self.database.sessions()
        self._transaction = self._session.begin()
        try:
            await self._transaction.__aenter__()
        except BaseException:
            await self._session.close()
            raise
        self.access = PostgresAccessRepository(self._session)
        self.audit = PostgresAuditRepository(self._session)
        self.accounts = PostgresAccountsRepository(self._session)
        self.credentials = PostgresCredentialsRepository(self._session)
        self.guests = PostgresGuestsRepository(self._session)
        self.sessions = PostgresSessionsRepository(self._session)
        self.attempts = PostgresAttemptsRepository(self._session)
        return self

    @asynccontextmanager
    async def savepoint(self):
        async with self._session.begin_nested():
            yield

    async def __aexit__(self, *exception):
        try:
            await self._transaction.__aexit__(*exception)
        finally:
            await self._session.close()


class PostgresUnitOfWorkFactory:
    def __init__(self, database: Database):
        self.database = database

    def __call__(self) -> PostgresUnitOfWork:
        return PostgresUnitOfWork(self.database)
