from uuid import UUID

from sqlalchemy import CheckConstraint, String
from sqlalchemy.dialects.postgresql import JSONB
from sqlalchemy.orm import Mapped, mapped_column

from ..base import Base, CreatedAtMixin


class RunRewardReceipt(CreatedAtMixin, Base):
    __tablename__ = "run_reward_receipts"
    run_id: Mapped[UUID] = mapped_column(primary_key=True)
    account_id: Mapped[UUID]
    configuration_id: Mapped[str] = mapped_column(String(80))
    # Historical grants without a retained message cannot reconstruct the original payload.
    payload_hash: Mapped[bytes | None]
    receipt_json: Mapped[dict] = mapped_column(JSONB)
    __table_args__ = (CheckConstraint("payload_hash IS NULL OR octet_length(payload_hash) = 32", name="hash_length"),)
