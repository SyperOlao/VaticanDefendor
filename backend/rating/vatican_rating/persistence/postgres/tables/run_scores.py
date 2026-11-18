from uuid import UUID
from sqlalchemy import BigInteger, CheckConstraint, ForeignKey, String
from sqlalchemy.dialects.postgresql import JSONB
from sqlalchemy.orm import Mapped, mapped_column
from ..base import Base, CreatedAtMixin


class RunScoreReceipt(CreatedAtMixin, Base):
    __tablename__ = 'run_score_receipts'
    run_id: Mapped[UUID] = mapped_column(primary_key=True)
    account_id: Mapped[UUID]
    board_id: Mapped[UUID] = mapped_column(ForeignKey('leaderboards.id'))
    ruleset_id: Mapped[str] = mapped_column(String(128))
    content_release_id: Mapped[str] = mapped_column(String(128))
    points: Mapped[int] = mapped_column(BigInteger)
    payload_hash: Mapped[bytes]
    receipt_json: Mapped[dict] = mapped_column(JSONB)
    __table_args__ = (CheckConstraint('points >= 0',name='points_nonnegative'),
                      CheckConstraint('octet_length(payload_hash) = 32',name='hash_length'))
