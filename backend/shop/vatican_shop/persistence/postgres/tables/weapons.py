from uuid import UUID
from sqlalchemy import CheckConstraint, ForeignKey, ForeignKeyConstraint, Index, Integer, String, UniqueConstraint, text
from sqlalchemy.dialects.postgresql import JSONB
from sqlalchemy.orm import Mapped, mapped_column
from ..base import Base, CreatedAtMixin


class PurchaseOperation(CreatedAtMixin, Base):
    """Existing prestige shop receipts; distinct from verified store payments."""
    __tablename__ = "purchase_operations"
    account_id: Mapped[UUID] = mapped_column(primary_key=True)
    operation_id: Mapped[UUID] = mapped_column(primary_key=True)
    offer_id: Mapped[str] = mapped_column(String(64))
    catalog_version: Mapped[str] = mapped_column(String(64))
    receipt_json: Mapped[dict] = mapped_column(JSONB)
    __table_args__ = (
        Index("purchase_operations_completed_offer_idx", "account_id", "offer_id",
              postgresql_where=text("receipt_json->>'status'='Completed'")),
        Index("purchase_operations_history_idx", "account_id", "created_at", "operation_id"),
        Index("purchase_operations_admin_history_idx", "created_at", "account_id", "operation_id"),
        Index("purchase_operations_admin_status_idx", text("(receipt_json->>'status')"),
              "created_at", "account_id", "operation_id"),
    )
