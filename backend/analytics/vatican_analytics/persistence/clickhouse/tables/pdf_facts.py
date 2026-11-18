"""Missing facts and player dimension from the PDF analytics ER (page 25)."""
from clickhouse_sqlalchemy import engines, types
from sqlalchemy import Column, func

from ..base import Base
from .facts import EventFactMixin


class DimPlayer(Base):
    __tablename__ = "dim_players"
    player_id = Column(types.UUID, primary_key=True)
    install_cohort = Column(types.Date, nullable=False)
    acquisition_channel = Column(types.String, nullable=False)
    platform = Column(types.LowCardinality(types.String), nullable=False)
    experiment_variant = Column(types.String, nullable=False)
    calculated_at = Column(types.DateTime64(3, "UTC"), server_default=func.now64(3), nullable=False)
    __table_args__ = (engines.ReplacingMergeTree(version="calculated_at", order_by=("player_id",)),)


class FactLevelGiftClaim(Base):
    __tablename__ = "fact_level_gift_claims"
    claim_id = Column(types.UUID, primary_key=True)
    player_id = Column(types.UUID, nullable=False)
    rewarded_level = Column(types.UInt32, nullable=False)
    reward_bundle_id = Column(types.UUID, nullable=False)
    claim_status = Column(types.LowCardinality(types.String), nullable=False)
    claimed_at = Column(types.Nullable(types.DateTime64(3, "UTC")))
    calculated_at = Column(types.DateTime64(3, "UTC"), server_default=func.now64(3), nullable=False)
    __table_args__ = (engines.ReplacingMergeTree(version="calculated_at", order_by=("claim_id",)),)


class FactProgression(EventFactMixin, Base):
    __tablename__ = "fact_progression"
    run_id = Column(types.Nullable(types.UUID))
    action = Column(types.LowCardinality(types.String), nullable=False)
    church_level = Column(types.Nullable(types.UInt16))
    prestige_points_delta = Column(types.Int64, nullable=False)
    prestige_balance = Column(types.Int64, nullable=False)
    upgrade_id = Column(types.Nullable(types.String))
    upgrade_level = Column(types.Nullable(types.UInt32))
    __table_args__ = (engines.ReplacingMergeTree(version="calculated_at", order_by=("event_id",)),)


class FactPlayerLevelUp(EventFactMixin, Base):
    __tablename__ = "fact_player_level_ups"
    run_id = Column(types.Nullable(types.UUID))
    old_level = Column(types.UInt32, nullable=False)
    new_level = Column(types.UInt32, nullable=False)
    total_xp = Column(types.UInt64, nullable=False)
    __table_args__ = (engines.ReplacingMergeTree(version="calculated_at", order_by=("event_id",)),)


class FactLeaderboardSnapshot(Base):
    __tablename__ = "fact_leaderboard_snapshots"
    snapshot_date = Column(types.Date, primary_key=True)
    leaderboard_id = Column(types.UUID, primary_key=True)
    player_id = Column(types.UUID, primary_key=True)
    total_xp = Column(types.UInt64, nullable=False)
    rank_position = Column(types.UInt64, nullable=False)
    current_level = Column(types.UInt32, nullable=False)
    calculated_at = Column(types.DateTime64(3, "UTC"), server_default=func.now64(3), nullable=False)
    __table_args__ = (engines.ReplacingMergeTree(version="calculated_at", order_by=("snapshot_date", "leaderboard_id", "player_id")),)


class FactBuildingPerformance(Base):
    __tablename__ = "fact_building_performance"
    run_id = Column(types.UUID, primary_key=True)
    building_type = Column(types.String, primary_key=True)
    count_built = Column(types.UInt32, nullable=False)
    count_destroyed = Column(types.UInt32, nullable=False)
    kills = Column(types.UInt32, nullable=False)
    damage_dealt = Column(types.UInt64, nullable=False)
    active_seconds = Column(types.UInt32, nullable=False)
    resources_produced = Column(types.UInt64, nullable=False)
    calculated_at = Column(types.DateTime64(3, "UTC"), server_default=func.now64(3), nullable=False)
    __table_args__ = (engines.ReplacingMergeTree(version="calculated_at", order_by=("run_id", "building_type")),)


class FactWave(Base):
    __tablename__ = "fact_waves"
    run_id = Column(types.UUID, primary_key=True)
    wave_number = Column(types.UInt32, primary_key=True)
    hero_kills = Column(types.UInt32, nullable=False)
    tower_kills = Column(types.UInt32, nullable=False)
    other_kills = Column(types.UInt32, nullable=False)
    hero_damage = Column(types.UInt64, nullable=False)
    tower_damage = Column(types.UInt64, nullable=False)
    other_damage = Column(types.UInt64, nullable=False)
    hero_deaths = Column(types.UInt32, nullable=False)
    buildings_alive = Column(types.UInt32, nullable=False)
    duration_seconds = Column(types.UInt32, nullable=False)
    calculated_at = Column(types.DateTime64(3, "UTC"), server_default=func.now64(3), nullable=False)
    __table_args__ = (engines.ReplacingMergeTree(version="calculated_at", order_by=("run_id", "wave_number")),)


class FactRunCombat(Base):
    __tablename__ = "fact_run_combat"
    run_id = Column(types.UUID, primary_key=True)
    hero_kills = Column(types.UInt32, nullable=False)
    hero_deaths = Column(types.UInt32, nullable=False)
    tower_kills = Column(types.UInt32, nullable=False)
    other_kills = Column(types.UInt32, nullable=False)
    hero_damage = Column(types.UInt64, nullable=False)
    tower_damage = Column(types.UInt64, nullable=False)
    other_damage = Column(types.UInt64, nullable=False)
    combat_opportunity_seconds = Column(types.UInt32, nullable=False)
    hero_active_seconds = Column(types.UInt32, nullable=False)
    buildings_placed = Column(types.UInt32, nullable=False)
    buildings_destroyed = Column(types.UInt32, nullable=False)
    calculated_at = Column(types.DateTime64(3, "UTC"), server_default=func.now64(3), nullable=False)
    __table_args__ = (engines.ReplacingMergeTree(version="calculated_at", order_by=("run_id",)),)
