from uuid import UUID

from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ....application.models import AccountRecord
from ..tables import Account


class PostgresAccountsRepository:
    def __init__(self, session: AsyncSession):
        self.session = session

    async def get(self, account_id: UUID, *, lock: bool = False) -> AccountRecord | None:
        statement = select(Account.id, Account.account_status).where(Account.id == account_id)
        if lock:
            statement = statement.with_for_update()
        row = (await self.session.execute(statement)).first()
        return AccountRecord(*row) if row else None

    async def create(self, account_id: UUID) -> None:
        self.session.add(Account(id=account_id))
        await self.session.flush()
