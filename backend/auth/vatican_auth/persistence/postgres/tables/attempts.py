from datetime import datetime
from uuid import UUID

from sqlalchemy import CheckConstraint, DateTime, ForeignKey, Integer, String, func
from sqlalchemy.orm import Mapped, mapped_column

from ..base import Base, CreatedAtMixin, IdentityMixin


class AuthAttempt(Base):
    __tablename__ = "auth_attempts"
    key_hash: Mapped[bytes] = mapped_column(primary_key=True)
    window_start: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())
    attempts: Mapped[int] = mapped_column(Integer)


class OAuthLoginAttempt(IdentityMixin, CreatedAtMixin, Base):
    __tablename__ = "oauth_login_attempts"
    provider: Mapped[str] = mapped_column(String(32))
    account_id: Mapped[UUID | None] = mapped_column(ForeignKey("accounts.id"))
    state_hash: Mapped[bytes] = mapped_column(unique=True)
    nonce_hash: Mapped[bytes] = mapped_column(unique=True)
    status: Mapped[str] = mapped_column(String(16), server_default="PENDING")
    expires_at: Mapped[datetime] = mapped_column(DateTime(timezone=True))
    completed_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    __table_args__ = (
        CheckConstraint("status IN ('PENDING','SUCCEEDED','FAILED','EXPIRED')", name="status"),
        CheckConstraint("octet_length(state_hash) = 32 AND octet_length(nonce_hash) = 32", name="hash_lengths"),
        CheckConstraint("expires_at > created_at", name="expiry"),
        CheckConstraint("status <> 'SUCCEEDED' OR (account_id IS NOT NULL AND completed_at IS NOT NULL)", name="successful_result"),
    )
