from datetime import datetime
from uuid import UUID

from sqlalchemy import delete, select
from sqlalchemy.ext.asyncio import AsyncSession

from ....application.models import GuestRecord
from ..tables import Account, GuestCredential


class PostgresGuestsRepository:
    def __init__(self, session: AsyncSession):
        self.session = session

    async def get(self, token_hash: bytes) -> GuestRecord | None:
        row = (await self.session.execute(select(
            GuestCredential.account_id, GuestCredential.expires_at, Account.account_status,
        ).join(Account, Account.id == GuestCredential.account_id)
            .where(GuestCredential.token_hash == token_hash))).first()
        return GuestRecord(*row) if row else None

    async def create(self, account_id: UUID, token_hash: bytes, expires_at: datetime) -> None:
        self.session.add(GuestCredential(account_id=account_id, token_hash=token_hash, expires_at=expires_at))

    async def delete(self, account_id: UUID, token_hash: bytes | None = None) -> None:
        statement = delete(GuestCredential).where(GuestCredential.account_id == account_id)
        if token_hash is not None:
            statement = statement.where(GuestCredential.token_hash == token_hash)
        await self.session.execute(statement)
