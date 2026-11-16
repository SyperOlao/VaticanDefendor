from uuid import UUID

from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ....application.models import AuditRecord
from ..tables import AdminAuditLog


class PostgresAuditRepository:
    def __init__(self, session: AsyncSession):
        self.session = session

    async def append(self, actor_id: UUID | None, target_id: UUID | None, action: str,
                     result: str, details: dict) -> None:
        self.session.add(AdminAuditLog(actor_account_id=actor_id, target_account_id=target_id,
                                       action=action, result=result, details=details))
        await self.session.flush()

    async def list(self, limit: int, offset: int) -> list[AuditRecord]:
        rows = (await self.session.scalars(select(AdminAuditLog).order_by(
            AdminAuditLog.created_at.desc(), AdminAuditLog.id.desc(),
        ).limit(limit).offset(offset))).all()
        return [AuditRecord(row.id, row.actor_account_id, row.target_account_id, row.action,
                            row.result, row.details, row.created_at) for row in rows]
