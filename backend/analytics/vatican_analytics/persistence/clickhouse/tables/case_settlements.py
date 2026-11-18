"""Confirmed case delivery without inferred duplicate-item telemetry."""
from clickhouse_sqlalchemy import engines, types
from sqlalchemy import Column, func
from ..base import Base


class FactCaseSettlement(Base):
    __tablename__='fact_case_settlements'
    opening_id=Column(types.UUID,primary_key=True)
    player_id=Column(types.UUID,nullable=False)
    case_id=Column(types.UUID,nullable=False)
    case_code=Column(types.String,nullable=False)
    ruleset_id=Column(types.String,nullable=False)
    reward_bundle_id=Column(types.UUID,nullable=False)
    payment_mode=Column(types.LowCardinality(types.String),nullable=False)
    rarity=Column(types.LowCardinality(types.String),nullable=False)
    pity_before=Column(types.UInt32,nullable=False)
    pity_after=Column(types.UInt32,nullable=False)
    guaranteed_triggered=Column(types.UInt8,nullable=False)
    entry_index=Column(types.UInt32,nullable=False)
    completed_at=Column(types.DateTime64(3,'UTC'),nullable=False)
    calculated_at=Column(types.DateTime64(3,'UTC'),nullable=False,server_default=func.now64(3))
    __table_args__=(engines.ReplacingMergeTree(version='calculated_at',order_by=('opening_id',)),)
