from uuid import UUID
from sqlalchemy import CheckConstraint, Integer
from sqlalchemy.dialects.postgresql import JSONB
from sqlalchemy.orm import Mapped, mapped_column
from ..base import Base, CreatedAtMixin


class LevelRewardReceipt(CreatedAtMixin, Base):
    __tablename__ = "level_reward_receipts"
    claim_id: Mapped[UUID] = mapped_column(primary_key=True)
    item_index: Mapped[int] = mapped_column(Integer, primary_key=True)
    account_id: Mapped[UUID]
    payload_hash: Mapped[bytes]
    receipt_json: Mapped[dict] = mapped_column(JSONB)
    __table_args__ = (CheckConstraint("item_index >= 0", name="index_nonnegative"),
                      CheckConstraint("octet_length(payload_hash) = 32", name="hash_length"))
