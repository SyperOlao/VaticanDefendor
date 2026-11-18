from uuid import UUID

from sqlalchemy import BigInteger, CheckConstraint
from sqlalchemy.dialects.postgresql import JSONB
from sqlalchemy.orm import Mapped, mapped_column

from ..base import Base, CreatedAtMixin


class RunRewardReceipt(CreatedAtMixin, Base):
    __tablename__ = "run_reward_receipts"
    run_id: Mapped[UUID] = mapped_column(primary_key=True)
    account_id: Mapped[UUID]
    amount: Mapped[int] = mapped_column(BigInteger)
    receipt_json: Mapped[dict] = mapped_column(JSONB)
    __table_args__ = (CheckConstraint("amount > 0", name="amount_positive"),)
