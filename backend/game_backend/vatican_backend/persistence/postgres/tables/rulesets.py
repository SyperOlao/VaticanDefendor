"""Published rule versions and validator limits (PDF, pp. 32 and 36)."""
from decimal import Decimal

from sqlalchemy import CheckConstraint, ForeignKey, Integer, Numeric, String, UniqueConstraint
from sqlalchemy.orm import Mapped, mapped_column

from ..base import Base, CreatedAtMixin


class Ruleset(CreatedAtMixin, Base):
    __tablename__ = "rulesets"
    id: Mapped[str] = mapped_column(String(128), primary_key=True)
    code: Mapped[str] = mapped_column(String(128))
    version: Mapped[int] = mapped_column(Integer, server_default="1")
    content_release_id: Mapped[str | None] = mapped_column(ForeignKey("content_releases.id"))
    status: Mapped[str] = mapped_column(String(16), server_default="DRAFT")
    __table_args__ = (
        UniqueConstraint("code", "version"),
        CheckConstraint("version > 0", name="version_positive"),
        CheckConstraint("status IN ('DRAFT','PUBLISHED','RETIRED')", name="status"),
    )


class RulesetLimit(Base):
    __tablename__ = "ruleset_limits"
    ruleset_id: Mapped[str] = mapped_column(ForeignKey("rulesets.id"), primary_key=True)
    rule_key: Mapped[str] = mapped_column(String(128), primary_key=True)
    target_key: Mapped[str] = mapped_column(String(128), primary_key=True, server_default="*")
    minimum: Mapped[Decimal | None] = mapped_column(Numeric(24, 6))
    maximum: Mapped[Decimal | None] = mapped_column(Numeric(24, 6))
    __table_args__ = (
        CheckConstraint("minimum IS NOT NULL OR maximum IS NOT NULL", name="bound_required"),
        CheckConstraint("minimum <= maximum", name="bounds_order"),
    )
