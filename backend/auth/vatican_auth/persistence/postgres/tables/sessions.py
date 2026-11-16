from datetime import datetime
from uuid import UUID

from sqlalchemy import DateTime, ForeignKey, Index
from sqlalchemy.orm import Mapped, mapped_column

from ..base import Base, CreatedAtMixin, IdentityMixin


class GuestCredential(CreatedAtMixin, Base):
    __tablename__ = "guest_credentials"
    token_hash: Mapped[bytes] = mapped_column(primary_key=True)
    account_id: Mapped[UUID] = mapped_column(ForeignKey("accounts.id"))
    expires_at: Mapped[datetime] = mapped_column(DateTime(timezone=True))
    __table_args__ = (Index("guest_credentials_account_idx", "account_id"),)


class AuthSession(IdentityMixin, CreatedAtMixin, Base):
    __tablename__ = "auth_sessions"
    account_id: Mapped[UUID] = mapped_column(ForeignKey("accounts.id"))
    access_hash: Mapped[bytes] = mapped_column(unique=True)
    access_expires_at: Mapped[datetime] = mapped_column(DateTime(timezone=True))
    refresh_hash: Mapped[bytes] = mapped_column(unique=True)
    refresh_expires_at: Mapped[datetime] = mapped_column(DateTime(timezone=True))
    revoked_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    __table_args__ = (Index("auth_sessions_account_idx", "account_id"),)
