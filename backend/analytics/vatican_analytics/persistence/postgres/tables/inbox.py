from datetime import datetime
from uuid import UUID
from sqlalchemy import CheckConstraint, DateTime, ForeignKeyConstraint, Index, Integer, String, func, text
from sqlalchemy.dialects.postgresql import JSONB
from sqlalchemy.orm import Mapped, mapped_column
from ..base import Base, CreatedAtMixin


class AnalyticsInbox(CreatedAtMixin, Base):
    __tablename__ = 'analytics_inbox'
    event_name: Mapped[str] = mapped_column(String(64),primary_key=True,server_default='run.analytics')
    source_id: Mapped[UUID] = mapped_column(primary_key=True)
    account_id: Mapped[UUID]
    payload_hash: Mapped[bytes]
    payload_json: Mapped[dict] = mapped_column(JSONB)
    status: Mapped[str] = mapped_column(String(16),server_default='PENDING')
    attempts: Mapped[int] = mapped_column(Integer,server_default='0')
    available_at: Mapped[datetime] = mapped_column(DateTime(timezone=True),server_default=func.now())
    lease_token: Mapped[UUID | None]
    last_error: Mapped[str | None] = mapped_column(String(64))
    receipt_json: Mapped[dict | None] = mapped_column(JSONB)
    completed_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    __table_args__=(CheckConstraint("event_name IN ('run.analytics','level-gift.analytics','case.analytics','shop.purchase.analytics','wallet.transaction.analytics')",name='event_type'),
        CheckConstraint('octet_length(payload_hash) = 32',name='hash_length'),
        CheckConstraint("status IN ('PENDING','COMPLETED','REVIEW') AND attempts >= 0",name='state'),
        CheckConstraint("(status = 'COMPLETED') = (completed_at IS NOT NULL AND receipt_json IS NOT NULL)",name='completion'),
        Index('ix_analytics_inbox_pending','available_at','created_at',postgresql_where=text("status = 'PENDING'")))


class ReceivedMessage(CreatedAtMixin, Base):
    __tablename__ = 'received_messages'
    message_id: Mapped[UUID] = mapped_column(primary_key=True)
    event_name: Mapped[str] = mapped_column(String(64),server_default='run.analytics')
    source_id: Mapped[UUID]
    payload_hash: Mapped[bytes]
    __table_args__=(CheckConstraint('octet_length(payload_hash) = 32',name='hash_length'),
        ForeignKeyConstraint(['event_name','source_id'],['analytics_inbox.event_name','analytics_inbox.source_id']))
