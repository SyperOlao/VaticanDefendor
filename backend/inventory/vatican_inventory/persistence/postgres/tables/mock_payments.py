from uuid import UUID
from sqlalchemy import CheckConstraint, Integer
from sqlalchemy.dialects.postgresql import JSONB
from sqlalchemy.orm import Mapped, mapped_column
from ..base import Base, CreatedAtMixin


class MockPaymentReceipt(CreatedAtMixin, Base):
    __tablename__ = 'mock_payment_receipts'
    transaction_id: Mapped[UUID] = mapped_column(primary_key=True)
    line_index: Mapped[int] = mapped_column(Integer, primary_key=True)
    unit_index: Mapped[int] = mapped_column(Integer, primary_key=True)
    account_id: Mapped[UUID]
    payload_hash: Mapped[bytes]
    receipt_json: Mapped[dict] = mapped_column(JSONB)
    __table_args__ = (
        CheckConstraint('line_index >= 0 AND unit_index >= 0', name='counters'),
        CheckConstraint('octet_length(payload_hash) = 32', name='hash_length'),
    )
