"""Game-owned profile data. Account UUIDs belong to the auth service."""
from datetime import datetime
from uuid import UUID

from sqlalchemy import BigInteger, CheckConstraint, DateTime, Integer, String, func
from sqlalchemy.orm import Mapped, mapped_column

from ..base import Base


class Profile(Base):
    __tablename__ = "profiles"
    account_id: Mapped[UUID] = mapped_column(primary_key=True)
    version: Mapped[int] = mapped_column(Integer)
    display_name: Mapped[str] = mapped_column(String(32))
    permanent_currency: Mapped[int] = mapped_column(BigInteger, server_default="500")
    wallet_revision: Mapped[int] = mapped_column(BigInteger, server_default="1")
    inventory_revision: Mapped[int] = mapped_column(BigInteger, server_default="1")
    updated_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())
    __table_args__ = (
        CheckConstraint("version > 0", name="version_positive"),
        CheckConstraint("permanent_currency >= 0", name="currency_nonnegative"),
        CheckConstraint("wallet_revision > 0", name="wallet_revision_positive"),
        CheckConstraint("inventory_revision > 0", name="inventory_revision_positive"),
    )


class AccountPreference(Base):
    __tablename__ = "account_preferences"
    account_id: Mapped[UUID] = mapped_column(primary_key=True)
    display_timezone_iana: Mapped[str] = mapped_column(String(64), server_default="UTC")
    locale: Mapped[str] = mapped_column(String(16), server_default="ru")
    avatar_asset_key: Mapped[str | None] = mapped_column(String(128))
