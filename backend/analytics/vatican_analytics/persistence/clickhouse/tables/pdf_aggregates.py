from clickhouse_sqlalchemy import engines, types
from sqlalchemy import Column, func

from ..base import Base


class AggRetentionCohort(Base):
    __tablename__ = "agg_retention_cohorts"
    cohort_day = Column(types.Date, primary_key=True)
    day_index = Column(types.UInt16, primary_key=True)
    cohort_users = Column(types.UInt64, nullable=False)
    active_users = Column(types.UInt64, nullable=False)
    calculated_at = Column(types.DateTime64(3, "UTC"), server_default=func.now64(3), nullable=False)
    __table_args__ = (engines.ReplacingMergeTree(version="calculated_at", order_by=("cohort_day", "day_index")),)


class AggPlayerXPDaily(Base):
    __tablename__ = "agg_player_xp_daily"
    day = Column(types.Date, primary_key=True)
    player_id = Column(types.UUID, primary_key=True)
    xp_awarded = Column(types.UInt64, nullable=False)
    hero_xp = Column(types.UInt64, nullable=False)
    tower_xp = Column(types.UInt64, nullable=False)
    tycoon_xp = Column(types.UInt64, nullable=False)
    victory_xp = Column(types.UInt64, nullable=False)
    calculated_at = Column(types.DateTime64(3, "UTC"), server_default=func.now64(3), nullable=False)
    __table_args__ = (engines.ReplacingMergeTree(version="calculated_at", order_by=("day", "player_id")),)


class MapAggregateMixin:
    day = Column(types.Date, primary_key=True)
    map_id = Column(types.String, primary_key=True)
    ruleset_id = Column(types.String, primary_key=True)
    difficulty = Column(types.String, primary_key=True)
    calculated_at = Column(types.DateTime64(3, "UTC"), server_default=func.now64(3), nullable=False)


class AggMapDaily(MapAggregateMixin, Base):
    __tablename__ = "agg_map_daily"
    attempts = Column(types.UInt64, nullable=False)
    wins = Column(types.UInt64, nullable=False)
    deaths = Column(types.UInt64, nullable=False)
    average_buildings_built = Column(types.Float64, nullable=False)
    average_alive_seconds = Column(types.Float64, nullable=False)
    __table_args__ = (engines.ReplacingMergeTree(version="calculated_at", order_by=("day", "map_id", "ruleset_id", "difficulty")),)


class AggCombatBalanceDaily(MapAggregateMixin, Base):
    __tablename__ = "agg_combat_balance_daily"
    runs = Column(types.UInt64, nullable=False)
    wins = Column(types.UInt64, nullable=False)
    average_hero_damage_share = Column(types.Float64, nullable=False)
    average_tower_damage_share = Column(types.Float64, nullable=False)
    average_building_count = Column(types.Float64, nullable=False)
    average_kills_per_minute = Column(types.Float64, nullable=False)
    __table_args__ = (engines.ReplacingMergeTree(version="calculated_at", order_by=("day", "map_id", "ruleset_id", "difficulty")),)
