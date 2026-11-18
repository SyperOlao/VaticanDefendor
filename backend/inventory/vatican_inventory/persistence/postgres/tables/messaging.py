from datetime import datetime
from uuid import UUID

from sqlalchemy import CheckConstraint, DateTime, ForeignKey, Index, Integer, String, Text, func, text
from sqlalchemy.dialects.postgresql import JSONB
from sqlalchemy.orm import Mapped, mapped_column

from ..base import Base, CreatedAtMixin, IdentityMixin


class OutboxMessage(IdentityMixin, CreatedAtMixin, Base):
    __tablename__ = "outbox_messages"
    destination: Mapped[str] = mapped_column(String(64))
    event_name: Mapped[str] = mapped_column(String(128))
    schema_version: Mapped[int] = mapped_column(Integer)
    aggregate_id: Mapped[UUID]
    payload: Mapped[dict] = mapped_column(JSONB)
    available_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())
    published_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    attempts: Mapped[int] = mapped_column(Integer, server_default="0")
    last_error: Mapped[str | None] = mapped_column(Text)
    lease_token: Mapped[UUID | None]
    failed_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    __table_args__ = (CheckConstraint("schema_version > 0 AND attempts >= 0", name="counters"),
                      Index("ix_outbox_pending", "available_at", "created_at", postgresql_where=text("published_at IS NULL")),
                      Index("ix_inventory_outbox_review", "created_at", "id", postgresql_where=text("failed_at IS NOT NULL")))


class ConsumedMessage(CreatedAtMixin, Base):
    __tablename__ = "consumed_messages"
    consumer: Mapped[str] = mapped_column(String(64), primary_key=True)
    message_id: Mapped[UUID] = mapped_column(primary_key=True)
    payload_hash: Mapped[bytes | None]
