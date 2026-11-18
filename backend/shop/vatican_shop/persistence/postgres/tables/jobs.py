from datetime import datetime
from uuid import UUID
from sqlalchemy import BigInteger, CheckConstraint, DateTime, ForeignKeyConstraint, Index, Integer, String, func, text
from sqlalchemy.orm import Mapped, mapped_column
from ..base import Base


class PurchaseJob(Base):
    __tablename__ = "purchase_jobs"
    account_id: Mapped[UUID] = mapped_column(primary_key=True)
    operation_id: Mapped[UUID] = mapped_column(primary_key=True)
    state: Mapped[str] = mapped_column(String(32), server_default="PENDING")
    lease_token: Mapped[UUID | None]
    available_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())
    attempts: Mapped[int] = mapped_column(Integer, server_default="0")
    version: Mapped[int] = mapped_column(BigInteger, server_default="1")
    last_error_code: Mapped[str | None] = mapped_column(String(64))
    __table_args__ = (
        ForeignKeyConstraint(["account_id", "operation_id"],
                             ["purchase_operations.account_id", "purchase_operations.operation_id"]),
        CheckConstraint("state IN ('PENDING','CHARGED','REFUND_PENDING','COMPLETED','REJECTED','REVIEW')", name="state"),
        CheckConstraint("attempts >= 0", name="attempts_nonnegative"),
        CheckConstraint("version > 0", name="version_positive"),
        Index("purchase_jobs_available_idx", "available_at",
              postgresql_where=text("state IN ('PENDING','CHARGED','REFUND_PENDING')")),
    )
