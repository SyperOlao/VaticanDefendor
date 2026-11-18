"""Confirmed virtual-currency purchases, separate from real-money store revenue."""
from clickhouse_sqlalchemy import engines, types
from sqlalchemy import Column, func
from ..base import Base


class FactShopPurchase(Base):
    __tablename__='fact_shop_purchases'
    purchase_id=Column(types.UUID,primary_key=True)
    operation_id=Column(types.UUID,nullable=False)
    player_id=Column(types.UUID,nullable=False)
    offer_id=Column(types.String,nullable=False)
    catalog_version=Column(types.String,nullable=False)
    currency_code=Column(types.LowCardinality(types.String),nullable=False)
    charged_amount=Column(types.UInt64,nullable=False)
    weapon_configuration_id=Column(types.String,nullable=False)
    weapon_instance_id=Column(types.UUID,nullable=False)
    completed_at=Column(types.DateTime64(3,'UTC'),nullable=False)
    calculated_at=Column(types.DateTime64(3,'UTC'),nullable=False,server_default=func.now64(3))
    __table_args__=(engines.ReplacingMergeTree(version='calculated_at',order_by=('purchase_id',)),)
