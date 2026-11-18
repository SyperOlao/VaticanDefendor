from datetime import datetime
from decimal import Decimal
from uuid import UUID

from sqlalchemy import BigInteger, CheckConstraint, DateTime, ForeignKey, ForeignKeyConstraint, Integer, Numeric, String, UniqueConstraint, func
from sqlalchemy.dialects.postgresql import JSONB
from sqlalchemy.orm import Mapped, mapped_column

from ..base import Base, CreatedAtMixin, IdentityMixin








class EnergyConfig(Base):
    __tablename__ = "energy_configs"
    ruleset_id: Mapped[str] = mapped_column(ForeignKey("rulesets.id"), primary_key=True)
    max_energy: Mapped[int] = mapped_column(Integer)
    regen_interval_ms: Mapped[int] = mapped_column(BigInteger)
    energy_per_tick: Mapped[int] = mapped_column(Integer)
    run_cost: Mapped[int | None] = mapped_column(Integer)
    __table_args__ = (CheckConstraint("max_energy > 0", name="max_positive"),
                      CheckConstraint("regen_interval_ms > 0", name="interval_positive"),
                      CheckConstraint("energy_per_tick > 0", name="tick_positive"),
                      CheckConstraint("run_cost IS NULL OR (run_cost >= 0 AND run_cost <= max_energy)", name="run_cost_range"))


class PlayerEnergy(Base):
    __tablename__ = "player_energy"
    account_id: Mapped[UUID] = mapped_column(primary_key=True)
    ruleset_id: Mapped[str] = mapped_column(ForeignKey("energy_configs.ruleset_id"))
    current_energy: Mapped[int] = mapped_column(BigInteger)
    last_calculated_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())
    regen_progress_ms: Mapped[int] = mapped_column(BigInteger, server_default="0")
    regen_fraction_ms: Mapped[Decimal] = mapped_column(Numeric(9, 9), server_default="0")
    state_version: Mapped[int] = mapped_column(BigInteger, server_default="1")
    __table_args__ = (CheckConstraint("current_energy >= 0", name="energy_nonnegative"),
                      CheckConstraint("regen_progress_ms >= 0", name="progress_nonnegative"),
                      CheckConstraint("state_version > 0", name="version_positive"),
                      CheckConstraint("regen_fraction_ms >= 0 AND regen_fraction_ms < 1", name="fraction_range"))


class PlayerEffect(IdentityMixin, CreatedAtMixin, Base):
    __tablename__ = "player_effects"
    account_id: Mapped[UUID] = mapped_column(index=True)
    effect_type: Mapped[str] = mapped_column(String(64))
    modifier_value: Mapped[Decimal] = mapped_column(Numeric(18, 6))
    source_key: Mapped[str] = mapped_column(String(128))
    starts_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())
    expires_at: Mapped[datetime] = mapped_column(DateTime(timezone=True))
    __table_args__ = (UniqueConstraint("account_id", "source_key"),
                      CheckConstraint("expires_at > starts_at", name="effect_window"))


class PrestigeUpgradeType(Base):
    __tablename__ = "prestige_upgrade_types"
    code: Mapped[str] = mapped_column(String(64), primary_key=True)


class PrestigeUpgradeDefinition(Base):
    __tablename__ = "prestige_upgrade_definitions"
    ruleset_id: Mapped[str] = mapped_column(ForeignKey("rulesets.id"), primary_key=True)
    code: Mapped[str] = mapped_column(ForeignKey("prestige_upgrade_types.code"), primary_key=True)
    level: Mapped[int] = mapped_column(Integer, primary_key=True)
    target: Mapped[str] = mapped_column(String(64))
    stat: Mapped[str] = mapped_column(String(64))
    modifier_value: Mapped[Decimal] = mapped_column(Numeric(18, 6))
    cost: Mapped[int] = mapped_column(BigInteger)
    __table_args__ = (CheckConstraint("level > 0", name="level_positive"),
                      CheckConstraint("cost >= 0", name="cost_nonnegative"))


class PlayerPrestigeUpgrade(Base):
    __tablename__ = "player_prestige_upgrades"
    account_id: Mapped[UUID] = mapped_column(primary_key=True)
    code: Mapped[str] = mapped_column(ForeignKey("prestige_upgrade_types.code"), primary_key=True)
    level: Mapped[int] = mapped_column(Integer)
    updated_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())
    bonus_snapshot: Mapped[dict | None] = mapped_column(JSONB(none_as_null=True))
    __table_args__ = (CheckConstraint("level > 0", name="level_positive"),)


class RunEnergyCharge(CreatedAtMixin, Base):
    __tablename__ = "run_energy_charges"
    run_id: Mapped[UUID] = mapped_column(ForeignKey("runs.id"), primary_key=True)
    account_id: Mapped[UUID]
    ruleset_id: Mapped[str] = mapped_column(ForeignKey("energy_configs.ruleset_id"))
    cost: Mapped[int] = mapped_column(Integer)
    refunded_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    __table_args__ = (
        ForeignKeyConstraint(["run_id", "account_id"], ["runs.id", "runs.account_id"], name="fk_run_energy_owner"),
        CheckConstraint("cost >= 0", name="cost_nonnegative"),
    )
