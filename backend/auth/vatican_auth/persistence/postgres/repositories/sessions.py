from datetime import datetime
from uuid import UUID

from sqlalchemy import select, update
from sqlalchemy.ext.asyncio import AsyncSession

from ....application.models import SessionRecord
from ..tables import Account, AuthSession


class PostgresSessionsRepository:
    def __init__(self, session: AsyncSession):
        self.session = session

    async def _find(self, condition, *, lock: bool = False) -> SessionRecord | None:
        statement = select(AuthSession.id, AuthSession.account_id, AuthSession.access_expires_at,
                           AuthSession.refresh_expires_at, AuthSession.revoked_at, Account.account_status
                           ).join(Account, Account.id == AuthSession.account_id).where(condition)
        if lock:
            statement = statement.with_for_update(of=AuthSession)
        row = (await self.session.execute(statement)).first()
        return SessionRecord(*row) if row else None

    async def by_access(self, token_hash: bytes) -> SessionRecord | None:
        return await self._find(AuthSession.access_hash == token_hash)

    async def by_refresh(self, token_hash: bytes, *, lock: bool = False) -> SessionRecord | None:
        return await self._find(AuthSession.refresh_hash == token_hash, lock=lock)

    async def create(self, session_id: UUID, account_id: UUID, access_hash: bytes, refresh_hash: bytes,
                     access_expires_at: datetime, refresh_expires_at: datetime) -> None:
        self.session.add(AuthSession(id=session_id, account_id=account_id, access_hash=access_hash,
                                    refresh_hash=refresh_hash, access_expires_at=access_expires_at,
                                    refresh_expires_at=refresh_expires_at))
        await self.session.flush()

    async def revoke(self, account_id: UUID, now: datetime, session_id: UUID | None = None) -> None:
        statement = update(AuthSession).where(AuthSession.account_id == account_id, AuthSession.revoked_at.is_(None))
        if session_id is not None:
            statement = statement.where(AuthSession.id == session_id)
        await self.session.execute(statement.values(revoked_at=now))
