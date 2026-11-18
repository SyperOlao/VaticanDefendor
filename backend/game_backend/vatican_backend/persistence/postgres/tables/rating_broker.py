from uuid import UUID
from sqlalchemy import CheckConstraint, Integer, String
from sqlalchemy.orm import Mapped, mapped_column
from ..base import Base, CreatedAtMixin


class RatingResultMessage(CreatedAtMixin, Base):
    __tablename__ = 'rating_result_messages'
    event_id: Mapped[UUID] = mapped_column(primary_key=True)
    payload_hash: Mapped[bytes]
    message_id: Mapped[UUID | None]
    generation: Mapped[int] = mapped_column(Integer, server_default='0')
    outcome: Mapped[str] = mapped_column(String(16))
    code: Mapped[str | None] = mapped_column(String(64))
    __table_args__ = (
        CheckConstraint('octet_length(payload_hash) = 32', name='hash_length'),
        CheckConstraint('generation >= 0', name='generation'),
        CheckConstraint("outcome IN ('APPLIED','REJECTED','REVIEW','IGNORED','QUARANTINED')", name='outcome'),
    )
