from datetime import datetime
from uuid import UUID

from sqlalchemy import BigInteger, CheckConstraint, DateTime, Index, Integer, String, func, text
from sqlalchemy.dialects.postgresql import JSON, JSONB
from sqlalchemy.orm import Mapped, mapped_column

from ..base import Base, CreatedAtMixin


class BrokerInbox(CreatedAtMixin, Base):
    __tablename__ = "broker_inbox"
    message_id: Mapped[UUID] = mapped_column(primary_key=True)
    envelope: Mapped[dict] = mapped_column(JSON)
    payload_hash: Mapped[bytes]
    status: Mapped[str] = mapped_column(String(16), server_default="PENDING")
    attempts: Mapped[int] = mapped_column(Integer, server_default="0")
    generation: Mapped[int] = mapped_column(Integer, server_default="1")
    available_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())
    lease_token: Mapped[UUID | None]
    last_error: Mapped[str | None] = mapped_column(String(64))
    result_json: Mapped[dict | None] = mapped_column(JSONB)
    finished_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    __table_args__ = (
        CheckConstraint("status IN ('PENDING','APPLIED','REJECTED','REVIEW')", name="status"),
        CheckConstraint("attempts >= 0 AND generation > 0", name="counters"),
        CheckConstraint("octet_length(payload_hash) = 32", name="hash_length"),
        CheckConstraint("(status = 'PENDING') = (finished_at IS NULL)", name="completion_time"),
        Index("ix_broker_inbox_pending", "available_at", "created_at", postgresql_where=text("status = 'PENDING'")),
        Index("ix_broker_inbox_inspect", "created_at", "message_id"),
        Index("ix_broker_inbox_status_inspect", "status", "created_at", "message_id"),
    )


class BrokerRejection(CreatedAtMixin, Base):
    __tablename__ = "broker_rejections"
    id: Mapped[UUID] = mapped_column(primary_key=True)
    message_id: Mapped[UUID | None]
    body_hash: Mapped[bytes]
    body_prefix: Mapped[bytes]
    body_size: Mapped[int] = mapped_column(BigInteger)
    code: Mapped[str] = mapped_column(String(64))
    __table_args__ = (
        CheckConstraint("octet_length(body_hash) = 32", name="hash_length"),
        CheckConstraint("body_size >= 0 AND octet_length(body_prefix) <= 4096", name="body_size"),
        Index("ix_broker_rejections_inspect", "created_at", "id"),
    )
