from clickhouse_sqlalchemy import engines, types
from sqlalchemy import Column, func

from ..base import Base


class EventFactMixin:
    event_id = Column(types.UUID, primary_key=True)
    player_id = Column(types.UUID, nullable=False)
    occurred_at = Column(types.DateTime64(3, "UTC"), nullable=False)
    calculated_at = Column(types.DateTime64(3, "UTC"), nullable=False, server_default=func.now64(3))


class FactSession(Base):
    __tablename__ = "fact_sessions"
    session_id = Column(types.UUID, primary_key=True)
    player_id = Column(types.UUID, nullable=False)
    started_at = Column(types.DateTime64(3, "UTC"), nullable=False)
    last_activity_at = Column(types.DateTime64(3, "UTC"), nullable=False)
    ended_at = Column(types.Nullable(types.DateTime64(3, "UTC")))
    close_reason = Column(types.Nullable(types.String))
    active_seconds = Column(types.UInt32, nullable=False)
    platform = Column(types.LowCardinality(types.String), nullable=False)
    app_version = Column(types.String, nullable=False)
    calculated_at = Column(types.DateTime64(3, "UTC"), nullable=False, server_default=func.now64(3))
    __table_args__ = (engines.ReplacingMergeTree(version="calculated_at", order_by=("session_id",)),)


class FactRun(Base):
    __tablename__ = "fact_runs"
    run_id = Column(types.UUID, primary_key=True)
    player_id = Column(types.UUID, nullable=False)
    session_id = Column(types.UUID, nullable=False)
    map_id = Column(types.LowCardinality(types.String), nullable=False)
    difficulty = Column(types.LowCardinality(types.String), nullable=False)
    ruleset_id = Column(types.String, nullable=False)
    result = Column(types.LowCardinality(types.String), nullable=False)
    end_reason = Column(types.LowCardinality(types.String), nullable=False)
    duration_seconds = Column(types.UInt32, nullable=False)
    church_level = Column(types.UInt16, nullable=False)
    hero_kills = Column(types.UInt32, nullable=False)
    tower_kills = Column(types.UInt32, nullable=False)
    hero_damage = Column(types.UInt64, nullable=False)
    tower_damage = Column(types.UInt64, nullable=False)
    xp_awarded = Column(types.UInt64, nullable=False)
    prestige_awarded = Column(types.UInt64, nullable=False)
    waves_reached = Column(types.Nullable(types.UInt32))
    active_seconds = Column(types.Nullable(types.UInt32))
    hero_deaths = Column(types.Nullable(types.UInt32))
    other_kills = Column(types.Nullable(types.UInt32))
    other_damage = Column(types.Nullable(types.UInt64))
    buildings_built = Column(types.Nullable(types.UInt32))
    buildings_lost = Column(types.Nullable(types.UInt32))
    occurred_at = Column(types.DateTime64(3, "UTC"), nullable=False)
    calculated_at = Column(types.DateTime64(3, "UTC"), nullable=False, server_default=func.now64(3))
    __table_args__ = (engines.ReplacingMergeTree(version="calculated_at", order_by=("run_id",)),)


class FactDeath(EventFactMixin, Base):
    __tablename__ = "fact_deaths"
    run_id = Column(types.UUID, nullable=False)
    map_id = Column(types.LowCardinality(types.String), nullable=False)
    x = Column(types.Float32, nullable=False)
    y = Column(types.Float32, nullable=False)
    wave = Column(types.UInt16, nullable=False)
    cause = Column(types.LowCardinality(types.String), nullable=False)
    enemy_type = Column(types.LowCardinality(types.String), nullable=False)
    weapon_configuration_id = Column(types.Nullable(types.String))
    __table_args__ = (engines.ReplacingMergeTree(version="calculated_at", order_by=("event_id",)),)


class FactEconomyTransaction(EventFactMixin, Base):
    __tablename__ = "fact_economy_transactions"
    transaction_id = Column(types.UUID, nullable=False)
    currency_code = Column(types.LowCardinality(types.String), nullable=False)
    amount_delta = Column(types.Int64, nullable=False)
    balance_after = Column(types.Int64, nullable=False)
    reason = Column(types.LowCardinality(types.String), nullable=False)
    __table_args__ = (engines.ReplacingMergeTree(version="calculated_at", order_by=("event_id",)),)


class FactPurchase(EventFactMixin, Base):
    __tablename__ = "fact_purchases"
    transaction_id = Column(types.UUID, nullable=False)
    order_id = Column(types.Nullable(types.UUID))
    product_sku = Column(types.String, nullable=False)
    provider = Column(types.LowCardinality(types.String), nullable=False)
    storefront = Column(types.LowCardinality(types.String), nullable=False)
    environment = Column(types.LowCardinality(types.String), nullable=False)
    currency_iso = Column(types.LowCardinality(types.String), nullable=False)
    amount_minor = Column(types.Int64, nullable=False)
    status = Column(types.LowCardinality(types.String), nullable=False)
    __table_args__ = (engines.ReplacingMergeTree(version="calculated_at", order_by=("event_id",)),)


class FactXPTransaction(EventFactMixin, Base):
    __tablename__ = "fact_xp_transactions"
    run_id = Column(types.UUID, nullable=False)
    source_type = Column(types.LowCardinality(types.String), nullable=False)
    xp_delta = Column(types.UInt64, nullable=False)
    level_after = Column(types.UInt32, nullable=False)
    xp_transaction_id = Column(types.Nullable(types.UUID))
    ruleset_id = Column(types.Nullable(types.String))
    hero_mob_kills = Column(types.Nullable(types.UInt32))
    tower_mob_kills = Column(types.Nullable(types.UInt32))
    building_upgrades = Column(types.Nullable(types.UInt32))
    hero_kill_xp = Column(types.Nullable(types.UInt64))
    tower_kill_xp = Column(types.Nullable(types.UInt64))
    tycoon_upgrade_xp = Column(types.Nullable(types.UInt64))
    victory_xp = Column(types.Nullable(types.UInt64))
    __table_args__ = (engines.ReplacingMergeTree(version="calculated_at", order_by=("event_id",)),)


class FactBuildingAction(EventFactMixin, Base):
    __tablename__ = "fact_building_actions"
    run_id = Column(types.UUID, nullable=False)
    building_type = Column(types.LowCardinality(types.String), nullable=False)
    action = Column(types.LowCardinality(types.String), nullable=False)
    level = Column(types.UInt16, nullable=False)
    wave_number = Column(types.Nullable(types.UInt32))
    __table_args__ = (engines.ReplacingMergeTree(version="calculated_at", order_by=("event_id",)),)


class FactCaseOpening(EventFactMixin, Base):
    __tablename__ = "fact_case_openings"
    case_code = Column(types.String, nullable=False)
    rarity = Column(types.LowCardinality(types.String), nullable=False)
    is_duplicate = Column(types.UInt8, nullable=False)
    pity_before = Column(types.UInt32, nullable=False)
    pity_after = Column(types.UInt32, nullable=False)
    run_id = Column(types.Nullable(types.UUID))
    weapon_configuration_id = Column(types.Nullable(types.String))
    guaranteed_triggered = Column(types.Nullable(types.UInt8))
    __table_args__ = (engines.ReplacingMergeTree(version="calculated_at", order_by=("event_id",)),)


class FactPerformanceSample(EventFactMixin, Base):
    __tablename__ = "fact_performance_samples"
    session_id = Column(types.UUID, nullable=False)
    fps_avg = Column(types.Float32, nullable=False)
    frame_time_p95_ms = Column(types.Float32, nullable=False)
    loading_ms = Column(types.UInt32, nullable=False)
    device_model = Column(types.String, nullable=False)
    peak_memory_mb = Column(types.Nullable(types.UInt64))
    error_code = Column(types.Nullable(types.String))
    __table_args__ = (engines.ReplacingMergeTree(version="calculated_at", order_by=("event_id",)),)
