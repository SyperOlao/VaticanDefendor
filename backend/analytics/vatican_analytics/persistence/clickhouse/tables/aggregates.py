"""Replace complete aggregate snapshots after deduplication; never sum raw retries."""
from clickhouse_sqlalchemy import engines, types
from sqlalchemy import Column, func

from ..base import Base


class AggPlayerDaily(Base):
    __tablename__ = "agg_player_daily"
    day = Column(types.Date, primary_key=True)
    player_id = Column(types.UUID, primary_key=True)
    sessions = Column(types.UInt32, nullable=False)
    active_seconds = Column(types.UInt64, nullable=False)
    runs = Column(types.UInt32, nullable=False)
    victories = Column(types.UInt32, nullable=False)
    deaths = Column(types.Nullable(types.UInt64))
    xp_awarded = Column(types.UInt64, nullable=False)
    calculated_at = Column(types.DateTime64(3, "UTC"), nullable=False, server_default=func.now64(3))
    __table_args__ = (engines.ReplacingMergeTree(version="calculated_at", order_by=("day", "player_id")),)


class AggRunDaily(Base):
    __tablename__ = "agg_run_daily"
    day = Column(types.Date, primary_key=True)
    map_id = Column(types.String, primary_key=True)
    difficulty = Column(types.String, primary_key=True)
    ruleset_id = Column(types.String, primary_key=True)
    runs = Column(types.UInt64, nullable=False)
    victories = Column(types.UInt64, nullable=False)
    total_duration_seconds = Column(types.UInt64, nullable=False)
    hero_damage = Column(types.UInt64, nullable=False)
    tower_damage = Column(types.UInt64, nullable=False)
    calculated_at = Column(types.DateTime64(3, "UTC"), nullable=False, server_default=func.now64(3))
    __table_args__ = (engines.ReplacingMergeTree(version="calculated_at", order_by=("day", "map_id", "difficulty", "ruleset_id")),)


class AggRevenueDaily(Base):
    __tablename__ = "agg_revenue_daily"
    day = Column(types.Date, primary_key=True)
    currency_iso = Column(types.String, primary_key=True)
    provider = Column(types.String, primary_key=True)
    environment = Column(types.String, primary_key=True)
    purchases = Column(types.UInt64, nullable=False)
    paying_players = Column(types.UInt64, nullable=False)
    gross_minor = Column(types.Int64, nullable=False)
    refund_minor = Column(types.Int64, nullable=False)
    calculated_at = Column(types.DateTime64(3, "UTC"), nullable=False, server_default=func.now64(3))
    __table_args__ = (engines.ReplacingMergeTree(version="calculated_at", order_by=("day", "currency_iso", "provider", "environment")),)
