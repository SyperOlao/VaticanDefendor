from datetime import datetime
from uuid import UUID

from sqlalchemy import DateTime, ForeignKey, String, Text, func
from sqlalchemy.orm import Mapped, mapped_column

from ..base import Base


class AccountLogin(Base):
    __tablename__ = "account_logins"
    login: Mapped[str] = mapped_column(String(64), primary_key=True)
    account_id: Mapped[UUID] = mapped_column(ForeignKey("accounts.id"), unique=True)
    password_hash: Mapped[str] = mapped_column(Text)
    recovery_hash: Mapped[bytes]
    updated_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())
