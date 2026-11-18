from datetime import datetime
from uuid import UUID

from sqlalchemy import CheckConstraint, DateTime, ForeignKey, String
from sqlalchemy.dialects.postgresql import JSONB
from sqlalchemy.orm import Mapped, mapped_column

from ..base import Base, CreatedAtMixin


class IdempotencyRecord(CreatedAtMixin, Base):
    __tablename__ = "idempotency_records"
    account_id: Mapped[UUID] = mapped_column(primary_key=True)
    operation: Mapped[str] = mapped_column(String(64), primary_key=True)
    request_key: Mapped[str] = mapped_column(String(128), primary_key=True)
    payload_hash: Mapped[bytes]
    status: Mapped[str] = mapped_column(String(16), server_default="PENDING")
    result_reference: Mapped[str | None] = mapped_column(String(255))
    result_json: Mapped[dict | None] = mapped_column(JSONB)
    completed_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    __table_args__ = (
        CheckConstraint("octet_length(payload_hash) = 32", name="hash_length"),
        CheckConstraint("status IN ('PENDING','UNKNOWN','COMPLETED','REJECTED')", name="status"),
        CheckConstraint("(status IN ('COMPLETED','REJECTED')) = (completed_at IS NOT NULL)", name="completion_time"),
    )
