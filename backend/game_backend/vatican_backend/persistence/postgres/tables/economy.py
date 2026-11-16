from datetime import datetime
from decimal import Decimal
from uuid import UUID

from sqlalchemy import BigInteger, CheckConstraint, DateTime, ForeignKey, Integer, Numeric, String, UniqueConstraint, func
from sqlalchemy.orm import Mapped, mapped_column

from ..base import Base, CreatedAtMixin, IdentityMixin


class Currency(Base):
    __tablename__ = "currencies"
    code: Mapped[str] = mapped_column(String(32), primary_key=True)


class CurrencyBalance(Base):
    """Additional wallets. Prestige remains in profiles until API cutover."""
    __tablename__ = "currency_balances"
    account_id: Mapped[UUID] = mapped_column(primary_key=True)
    currency_code: Mapped[str] = mapped_column(ForeignKey("currencies.code"), primary_key=True)
    balance: Mapped[int] = mapped_column(BigInteger, server_default="0")
    revision: Mapped[int] = mapped_column(BigInteger, server_default="1")
    __table_args__ = (CheckConstraint("balance >= 0", name="balance_nonnegative"),
                      CheckConstraint("revision > 0", name="revision_positive"),
                      CheckConstraint("currency_code <> 'PRESTIGE'", name="prestige_in_profile"))


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
        UniqueConstraint("id", "account_id"),
        UniqueConstraint("account_id", "currency_code", "source_type", "source_id", "line_index", name="uq_economy_transaction_source"),
        CheckConstraint("delta <> 0", name="delta_nonzero"),
        CheckConstraint("balance_after >= 0", name="balance_nonnegative"),
        CheckConstraint("line_index >= 0", name="line_nonnegative"),
    )


class EnergyConfig(Base):
    __tablename__ = "energy_configs"
    ruleset_id: Mapped[str] = mapped_column(ForeignKey("rulesets.id"), primary_key=True)
    max_energy: Mapped[int] = mapped_column(Integer)
    regen_interval_ms: Mapped[int] = mapped_column(BigInteger)
    energy_per_tick: Mapped[int] = mapped_column(Integer)
    __table_args__ = (CheckConstraint("max_energy > 0", name="max_positive"),
                      CheckConstraint("regen_interval_ms > 0", name="interval_positive"),
                      CheckConstraint("energy_per_tick > 0", name="tick_positive"))


class PlayerEnergy(Base):
    __tablename__ = "player_energy"
    account_id: Mapped[UUID] = mapped_column(primary_key=True)
    ruleset_id: Mapped[str] = mapped_column(ForeignKey("energy_configs.ruleset_id"))
    current_energy: Mapped[int] = mapped_column(Integer)
    last_calculated_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())
    regen_progress_ms: Mapped[int] = mapped_column(BigInteger, server_default="0")
    state_version: Mapped[int] = mapped_column(BigInteger, server_default="1")
    __table_args__ = (CheckConstraint("current_energy >= 0", name="energy_nonnegative"),
                      CheckConstraint("regen_progress_ms >= 0", name="progress_nonnegative"),
                      CheckConstraint("state_version > 0", name="version_positive"))


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
    __table_args__ = (CheckConstraint("level > 0", name="level_positive"),)
