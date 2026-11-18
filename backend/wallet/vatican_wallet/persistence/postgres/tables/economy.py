from datetime import datetime
from decimal import Decimal
from uuid import UUID
from sqlalchemy import BigInteger, CheckConstraint, DateTime, ForeignKey, Index, Integer, Numeric, String, UniqueConstraint, func, text
from sqlalchemy.orm import Mapped, mapped_column
from ..base import Base, CreatedAtMixin, IdentityMixin


class Currency(Base):
    __tablename__ = "currencies"
    code: Mapped[str] = mapped_column(String(32), primary_key=True)


class CurrencyBalance(Base):
    """The sole source of all persistent currency balances, including prestige."""
    __tablename__ = "currency_balances"
    account_id: Mapped[UUID] = mapped_column(primary_key=True)
    currency_code: Mapped[str] = mapped_column(ForeignKey("currencies.code"), primary_key=True)
    balance: Mapped[int] = mapped_column(BigInteger, server_default="0")
    revision: Mapped[int] = mapped_column(BigInteger, server_default="1")
    __table_args__ = (CheckConstraint("balance >= 0", name="balance_nonnegative"),
                      CheckConstraint("revision > 0", name="revision_positive"))


class EconomyTransaction(IdentityMixin, CreatedAtMixin, Base):
    __tablename__ = "economy_transactions"
    account_id: Mapped[UUID] = mapped_column(index=True)
    currency_code: Mapped[str] = mapped_column(ForeignKey("currencies.code"))
    delta: Mapped[int] = mapped_column(BigInteger)
    balance_after: Mapped[int] = mapped_column(BigInteger)
    reason: Mapped[str] = mapped_column(String(64))
    source_type: Mapped[str] = mapped_column(String(32))
    source_id: Mapped[UUID]
    line_index: Mapped[int] = mapped_column(Integer, server_default="0")
    __table_args__ = (
        Index("ix_economy_history", "account_id", "created_at", "id"),
        Index("uq_economy_run_source", "source_id", unique=True,
              postgresql_where=text("source_type = 'RUN_REWARD'")),
        UniqueConstraint("id", "account_id"),
        UniqueConstraint("account_id", "currency_code", "source_type", "source_id", "line_index", name="uq_economy_transaction_source"),
        CheckConstraint("delta <> 0", name="delta_nonzero"),
        CheckConstraint("balance_after >= 0", name="balance_nonnegative"),
        CheckConstraint("line_index >= 0", name="line_nonnegative"),
    )
