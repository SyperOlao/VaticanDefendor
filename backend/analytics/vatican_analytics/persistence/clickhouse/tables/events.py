from clickhouse_sqlalchemy import engines, types
from sqlalchemy import Column, func

from ..base import Base


class RawGameEvent(Base):
    __tablename__ = "raw_game_events"
    event_id = Column(types.UUID, primary_key=True)
    player_id = Column(types.UUID, nullable=False)
    session_id = Column(types.Nullable(types.UUID))
    run_id = Column(types.Nullable(types.UUID))
    event_name = Column(types.LowCardinality(types.String), nullable=False)
    source = Column(types.LowCardinality(types.String), nullable=False)
    schema_version = Column(types.UInt16, nullable=False)
    app_version = Column(types.Nullable(types.String))
    occurred_at = Column(types.DateTime64(3, "UTC"), nullable=False)
    received_at = Column(types.DateTime64(3, "UTC"), nullable=False, server_default=func.now64(3))
    payload_json = Column(types.String, nullable=False)
    # Stable identity across retries, including retries arriving in another month.
    # ReplacingMergeTree is eventually consistent: read FINAL before aggregation.
    __table_args__ = (engines.ReplacingMergeTree(version="received_at", order_by=("event_id",)),)
