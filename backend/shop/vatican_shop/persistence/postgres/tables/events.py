from datetime import datetime
from uuid import UUID

from sqlalchemy import BigInteger, CheckConstraint, DateTime, ForeignKeyConstraint, Integer, String, func
from sqlalchemy.orm import Mapped, mapped_column

from ..base import Base


class PurchaseEvent(Base):
    __tablename__ = "purchase_events"
    account_id: Mapped[UUID] = mapped_column(primary_key=True)
    operation_id: Mapped[UUID] = mapped_column(primary_key=True)
    sequence: Mapped[int] = mapped_column(BigInteger, primary_key=True)
    kind: Mapped[str] = mapped_column(String(32))
    from_state: Mapped[str | None] = mapped_column(String(32))
    state: Mapped[str] = mapped_column(String(32))
    attempts: Mapped[int | None] = mapped_column(Integer)
    error_code: Mapped[str | None] = mapped_column(String(64))
    actor_id: Mapped[UUID | None]
    request_key: Mapped[UUID | None]
    reason: Mapped[str | None] = mapped_column(String(500))
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())
    __table_args__ = (
        ForeignKeyConstraint(["account_id", "operation_id"],
                             ["purchase_operations.account_id", "purchase_operations.operation_id"]),
        CheckConstraint("sequence > 0 AND (attempts IS NULL OR attempts >= 0)", name="counters"),
        CheckConstraint("kind IN ('CREATED','CLAIMED','CHARGED','REFUND_PENDING','COMPLETED','REJECTED','RETRY_SCHEDULED',"
                        "'REVIEW','ADMIN_RETRY','CLI_RETRY','LEGACY_SNAPSHOT')", name="kind"),
        CheckConstraint("state IN ('PENDING','CHARGED','REFUND_PENDING','COMPLETED','REJECTED','REVIEW')", name="state"),
        CheckConstraint("kind != 'ADMIN_RETRY' OR (actor_id IS NOT NULL AND request_key IS NOT NULL "
                        "AND reason IS NOT NULL AND length(btrim(reason)) > 0)", name="admin_actor"),
    )
