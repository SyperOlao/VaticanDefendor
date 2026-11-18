from datetime import datetime
from uuid import UUID

from sqlalchemy import BigInteger, Boolean, CheckConstraint, DateTime, ForeignKey, ForeignKeyConstraint, Index, Integer, String, func
from sqlalchemy.dialects.postgresql import JSONB
from sqlalchemy.orm import Mapped, mapped_column
from ..base import Base, CreatedAtMixin


class MockProduct(Base):
    __tablename__ = 'mock_products'
    product_id: Mapped[UUID] = mapped_column(ForeignKey('shop_products.id'), primary_key=True)
    mapping_id: Mapped[UUID] = mapped_column(ForeignKey('store_product_mappings.id'), unique=True)
    configuration_hash: Mapped[bytes]
    snapshot: Mapped[dict] = mapped_column(JSONB)
    __table_args__ = (CheckConstraint('octet_length(configuration_hash) = 32', name='hash_length'),)


class MockOrderState(Base):
    __tablename__ = 'mock_order_states'
    order_id: Mapped[UUID] = mapped_column(ForeignKey('payment_orders.id'), primary_key=True)
    snapshot: Mapped[dict] = mapped_column(JSONB)
    payload_hash: Mapped[bytes]
    transaction_id: Mapped[UUID | None] = mapped_column(ForeignKey('purchase_transactions.id'))
    fulfillment_status: Mapped[str] = mapped_column(String(32), server_default='NOT_READY')
    version: Mapped[int] = mapped_column(BigInteger, server_default='1')
    last_provider_version: Mapped[int] = mapped_column(Integer, server_default='0')
    last_provider_status: Mapped[str] = mapped_column(String(16), server_default='PENDING')
    verify_enabled: Mapped[bool] = mapped_column(Boolean, server_default='true')
    verify_pending: Mapped[bool] = mapped_column(Boolean, server_default='true')
    available_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())
    lease_token: Mapped[UUID | None]
    attempts: Mapped[int] = mapped_column(Integer, server_default='0')
    last_error: Mapped[str | None] = mapped_column(String(64))
    review_code: Mapped[str | None] = mapped_column(String(64))
    __table_args__ = (
        CheckConstraint('octet_length(payload_hash) = 32', name='hash_length'),
        CheckConstraint('version > 0 AND last_provider_version >= 0 AND attempts >= 0', name='counters'),
        CheckConstraint("fulfillment_status IN ('NOT_READY','PENDING','PARTIAL','COMPLETED','CANCELLED','REVIEW')", name='fulfillment'),
        Index('mock_orders_verify_idx', 'verify_enabled', 'available_at'),
    )


class MockProviderPayment(CreatedAtMixin, Base):
    __tablename__ = 'mock_provider_payments'
    order_id: Mapped[UUID] = mapped_column(ForeignKey('payment_orders.id'), primary_key=True)
    truth_json: Mapped[dict] = mapped_column(JSONB)
    unavailable: Mapped[bool] = mapped_column(Boolean, server_default='false')
    updated_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())


class MockFulfillmentJob(CreatedAtMixin, Base):
    __tablename__ = 'mock_fulfillment_jobs'
    order_id: Mapped[UUID] = mapped_column(ForeignKey('payment_orders.id'), primary_key=True)
    line_index: Mapped[int] = mapped_column(Integer, primary_key=True)
    unit_index: Mapped[int] = mapped_column(Integer, primary_key=True)
    transaction_id: Mapped[UUID] = mapped_column(ForeignKey('purchase_transactions.id'))
    configuration_id: Mapped[str] = mapped_column(String(80))
    state: Mapped[str] = mapped_column(String(32), server_default='PENDING')
    lease_token: Mapped[UUID | None]
    available_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())
    attempts: Mapped[int] = mapped_column(Integer, server_default='0')
    last_error: Mapped[str | None] = mapped_column(String(64))
    receipt_json: Mapped[dict | None] = mapped_column(JSONB)
    __table_args__ = (
        CheckConstraint('line_index >= 0 AND unit_index >= 0 AND attempts >= 0', name='counters'),
        CheckConstraint("state IN ('PENDING','COMPLETED','REVIEW','CANCELLED')", name='state'),
        Index('mock_fulfillment_available_idx', 'state', 'available_at'),
    )
