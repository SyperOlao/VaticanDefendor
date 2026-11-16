from datetime import datetime
from uuid import UUID

from sqlalchemy import Boolean, DateTime, ForeignKey, Index, Integer, String, Text, func, text
from sqlalchemy.dialects.postgresql import JSONB
from sqlalchemy.orm import Mapped, mapped_column

from ..base import Base, CreatedAtMixin, IdentityMixin


class Role(Base):
    __tablename__ = "roles"
    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    code: Mapped[str] = mapped_column(String(64), unique=True)
    description: Mapped[str] = mapped_column(Text)
    is_assignable: Mapped[bool] = mapped_column(Boolean, server_default="true")


class Permission(Base):
    __tablename__ = "permissions"
    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    code: Mapped[str] = mapped_column(String(128), unique=True)
    description: Mapped[str] = mapped_column(Text)


class RolePermission(Base):
    __tablename__ = "role_permissions"
    role_id: Mapped[int] = mapped_column(ForeignKey("roles.id"), primary_key=True)
    permission_id: Mapped[int] = mapped_column(ForeignKey("permissions.id"), primary_key=True)


class AccountRole(IdentityMixin, Base):
    __tablename__ = "account_roles"
    account_id: Mapped[UUID] = mapped_column(ForeignKey("accounts.id"))
    role_id: Mapped[int] = mapped_column(ForeignKey("roles.id"))
    granted_by: Mapped[UUID | None] = mapped_column(ForeignKey("accounts.id"))
    granted_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())
    revoked_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    __table_args__ = (Index("uq_account_roles_active", "account_id", "role_id", unique=True,
                            postgresql_where=text("revoked_at IS NULL")),)


class AdminAuditLog(IdentityMixin, CreatedAtMixin, Base):
    __tablename__ = "admin_audit_logs"
    actor_account_id: Mapped[UUID | None] = mapped_column(ForeignKey("accounts.id"))
    target_account_id: Mapped[UUID | None] = mapped_column(ForeignKey("accounts.id"))
    action: Mapped[str] = mapped_column(String(128))
    result: Mapped[str] = mapped_column(String(32))
    details: Mapped[dict] = mapped_column(JSONB, server_default="{}")
