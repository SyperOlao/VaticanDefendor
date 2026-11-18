from uuid import UUID

from sqlalchemy import ForeignKey, String, UniqueConstraint
from sqlalchemy.orm import Mapped, mapped_column

from ..base import Base, CreatedAtMixin, IdentityMixin


class AuthIdentity(IdentityMixin, CreatedAtMixin, Base):
    __tablename__ = "auth_identities"
    account_id: Mapped[UUID] = mapped_column(ForeignKey("accounts.id"), index=True)
    provider: Mapped[str] = mapped_column(String(32))
    issuer: Mapped[str] = mapped_column(String(512))
    subject: Mapped[str] = mapped_column(String(255))
    __table_args__ = (UniqueConstraint("issuer", "subject"),)
