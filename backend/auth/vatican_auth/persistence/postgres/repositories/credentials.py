from datetime import datetime
from uuid import UUID

from sqlalchemy import func, select, update
from sqlalchemy.ext.asyncio import AsyncSession

from ....application.models import CredentialsRecord
from ..tables import AccountLogin


class PostgresCredentialsRepository:
    def __init__(self, session: AsyncSession):
        self.session = session

    async def lock_login(self, login: str) -> None:
        await self.session.execute(select(func.pg_advisory_xact_lock(func.hashtextextended(login, 0))))

    async def _find(self, condition) -> CredentialsRecord | None:
        row = (await self.session.execute(select(
            AccountLogin.account_id, AccountLogin.login, AccountLogin.password_hash, AccountLogin.recovery_hash,
        ).where(condition))).first()
        return CredentialsRecord(*row) if row else None

    async def by_login(self, login: str) -> CredentialsRecord | None:
        return await self._find(AccountLogin.login == login)

    async def by_account(self, account_id: UUID) -> CredentialsRecord | None:
        return await self._find(AccountLogin.account_id == account_id)

    async def create(self, account_id: UUID, login: str, password_hash: str, recovery_hash: bytes) -> None:
        self.session.add(AccountLogin(account_id=account_id, login=login,
                                      password_hash=password_hash, recovery_hash=recovery_hash))
        await self.session.flush()

    async def update(self, account_id: UUID, password_hash: str, recovery_hash: bytes, now: datetime) -> None:
        await self.session.execute(update(AccountLogin).where(AccountLogin.account_id == account_id).values(
            password_hash=password_hash, recovery_hash=recovery_hash, updated_at=now,
        ))

    async def rehash(self, account_id: UUID, password_hash: str, now: datetime) -> None:
        await self.session.execute(update(AccountLogin).where(AccountLogin.account_id == account_id).values(
            password_hash=password_hash, updated_at=now,
        ))
