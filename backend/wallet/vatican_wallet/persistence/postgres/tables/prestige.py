from uuid import UUID
from sqlalchemy import CheckConstraint
from sqlalchemy.dialects.postgresql import JSONB
from sqlalchemy.orm import Mapped,mapped_column
from ..base import Base,CreatedAtMixin


class PrestigePaymentReceipt(CreatedAtMixin,Base):
    __tablename__='prestige_payment_receipts'
    purchase_id: Mapped[UUID]=mapped_column(primary_key=True)
    account_id: Mapped[UUID]
    payload_hash: Mapped[bytes]
    receipt_json: Mapped[dict]=mapped_column(JSONB)
    __table_args__=(CheckConstraint('octet_length(payload_hash) = 32',name='hash_length'),)
