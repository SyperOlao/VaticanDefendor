"""Authoritative run summary; unavailable simulation telemetry is not fabricated."""
from clickhouse_sqlalchemy import engines, types
from sqlalchemy import Column, func, text
from ..base import Base


class FactRunSettlement(Base):
    __tablename__='fact_run_settlements'
    run_id=Column(types.UUID,primary_key=True)
    player_id=Column(types.UUID,nullable=False)
    ruleset_id=Column(types.String,nullable=False)
    content_release_id=Column(types.String,nullable=False)
    simulation_mode=Column(types.LowCardinality(types.String),nullable=False,server_default=text("'DEDICATED'"))
    outcome=Column(types.LowCardinality(types.String),nullable=False)
    church_level=Column(types.UInt16,nullable=False)
    hero_kills=Column(types.UInt64,nullable=False)
    tower_kills=Column(types.UInt64,nullable=False)
    building_upgrades=Column(types.UInt64,nullable=False)
    xp_granted=Column(types.Nullable(types.UInt64))
    total_xp=Column(types.Nullable(types.UInt64))
    level_after=Column(types.Nullable(types.UInt32))
    occurred_at=Column(types.DateTime64(3,'UTC'),nullable=False)
    calculated_at=Column(types.DateTime64(3,'UTC'),nullable=False,server_default=func.now64(3))
    __table_args__=(engines.ReplacingMergeTree(version='calculated_at',order_by=('run_id',)),)
