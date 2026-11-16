from datetime import datetime, timedelta

from sqlalchemy import case
from sqlalchemy.dialects.postgresql import insert
from sqlalchemy.ext.asyncio import AsyncSession

from ....application.models import AttemptRecord
from ..tables import AuthAttempt


class PostgresAttemptsRepository:
    def __init__(self, session: AsyncSession):
        self.session = session

    async def record(self, key_hash: bytes, now: datetime, window: timedelta) -> AttemptRecord:
        expired = AuthAttempt.window_start <= now - window
        statement = insert(AuthAttempt).values(key_hash=key_hash, attempts=1, window_start=now)
        row = (await self.session.execute(statement.on_conflict_do_update(
            index_elements=[AuthAttempt.key_hash], set_={
                "attempts": case((expired, 1), else_=AuthAttempt.attempts + 1),
                "window_start": case((expired, now), else_=AuthAttempt.window_start),
            },
        ).returning(AuthAttempt.attempts, AuthAttempt.window_start))).one()
        return AttemptRecord(*row)
