"""Verified financial state only; storefronts, providers and payment methods differ."""
from datetime import datetime
from uuid import UUID

from sqlalchemy import BigInteger, Boolean, CheckConstraint, DateTime, ForeignKey, ForeignKeyConstraint, Integer, String, UniqueConstraint
from sqlalchemy.orm import Mapped, mapped_column

from ..base import Base, CreatedAtMixin, IdentityMixin


class Storefront(Base):
    __tablename__ = "storefronts"
    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    code: Mapped[str] = mapped_column(String(32), unique=True)
    platform: Mapped[str] = mapped_column(String(32))
    enabled: Mapped[bool] = mapped_column(Boolean, server_default="false")


class PaymentProvider(Base):
    __tablename__ = "payment_providers"
    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    code: Mapped[str] = mapped_column(String(32), unique=True)
    provider_kind: Mapped[str] = mapped_column(String(32))
    enabled: Mapped[bool] = mapped_column(Boolean, server_default="false")


class PaymentMethod(Base):
    __tablename__ = "payment_methods"
    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    code: Mapped[str] = mapped_column(String(32), unique=True)
    kind: Mapped[str] = mapped_column(String(32))


class PaymentRoutingRule(IdentityMixin, Base):
    __tablename__ = "payment_routing_rules"
    storefront_id: Mapped[int] = mapped_column(ForeignKey("storefronts.id"))
    provider_id: Mapped[int] = mapped_column(ForeignKey("payment_providers.id"))
    payment_method_id: Mapped[int | None] = mapped_column(ForeignKey("payment_methods.id"))
    country_iso: Mapped[str] = mapped_column(String(2))
    app_package: Mapped[str] = mapped_column(String(255))
    priority: Mapped[int] = mapped_column(Integer)
    enabled: Mapped[bool] = mapped_column(Boolean, server_default="false")


class ShopProduct(IdentityMixin, Base):
    __tablename__ = "shop_products"
    internal_sku: Mapped[str] = mapped_column(String(128))
    version: Mapped[int] = mapped_column(Integer)
    reward_bundle_id: Mapped[UUID] = mapped_column(ForeignKey("reward_bundles.id"))
    product_type: Mapped[str] = mapped_column(String(32))
    enabled: Mapped[bool] = mapped_column(Boolean, server_default="false")
    __table_args__ = (UniqueConstraint("internal_sku", "version"),
                      CheckConstraint("version > 0", name="version_positive"),
                      CheckConstraint("product_type IN ('CONSUMABLE','NON_CONSUMABLE','SUBSCRIPTION')", name="product_type"))


class StoreProductMapping(IdentityMixin, Base):
    __tablename__ = "store_product_mappings"
    product_id: Mapped[UUID] = mapped_column(ForeignKey("shop_products.id"))
    storefront_id: Mapped[int] = mapped_column(ForeignKey("storefronts.id"))
    provider_id: Mapped[int] = mapped_column(ForeignKey("payment_providers.id"))
    external_sku: Mapped[str] = mapped_column(String(255))
    environment: Mapped[str] = mapped_column(String(16))
    __table_args__ = (UniqueConstraint("provider_id", "storefront_id", "environment", "external_sku"),
                      CheckConstraint("environment IN ('TEST','PRODUCTION')", name="environment"))


class PaymentOrder(IdentityMixin, CreatedAtMixin, Base):
    __tablename__ = "payment_orders"
    account_id: Mapped[UUID] = mapped_column(index=True)
    product_id: Mapped[UUID] = mapped_column(ForeignKey("shop_products.id"))
    storefront_id: Mapped[int] = mapped_column(ForeignKey("storefronts.id"))
    provider_id: Mapped[int] = mapped_column(ForeignKey("payment_providers.id"))
    environment: Mapped[str] = mapped_column(String(16))
    country_iso: Mapped[str | None] = mapped_column(String(2))
    app_package: Mapped[str | None] = mapped_column(String(255))
    client_request_key: Mapped[UUID]
    currency_iso: Mapped[str] = mapped_column(String(3))
    price_minor: Mapped[int] = mapped_column(BigInteger)
    status: Mapped[str] = mapped_column(String(16), server_default="PENDING")
    __table_args__ = (UniqueConstraint("account_id", "client_request_key"),
                      UniqueConstraint("id", "provider_id", "environment", name="uq_payment_order_provider_environment"),
                      CheckConstraint("price_minor >= 0", name="price_nonnegative"),
                      CheckConstraint("environment IN ('TEST','PRODUCTION')", name="environment"),
                      CheckConstraint("status IN ('PENDING','UNKNOWN','VERIFIED','DECLINED','FULFILLED','REFUNDED','CANCELLED')", name="status"))


class PaymentAttempt(IdentityMixin, CreatedAtMixin, Base):
    __tablename__ = "payment_attempts"
    order_id: Mapped[UUID] = mapped_column(ForeignKey("payment_orders.id"), index=True)
    external_payment_id: Mapped[str | None] = mapped_column(String(255))
    status: Mapped[str] = mapped_column(String(32))


class ProviderNotification(IdentityMixin, CreatedAtMixin, Base):
    __tablename__ = "provider_notification_inbox"
    provider_id: Mapped[int] = mapped_column(ForeignKey("payment_providers.id"))
    environment: Mapped[str] = mapped_column(String(16))
    external_event_id: Mapped[str | None] = mapped_column(String(255))
    payload_hash: Mapped[bytes]
    status: Mapped[str] = mapped_column(String(32), server_default="PENDING")
    processed_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    __table_args__ = (UniqueConstraint("provider_id", "environment", "external_event_id"),
                      CheckConstraint("octet_length(payload_hash) = 32", name="hash_length"),
                      CheckConstraint("environment IN ('TEST','PRODUCTION')", name="environment"))


class PurchaseTransaction(IdentityMixin, CreatedAtMixin, Base):
    __tablename__ = "purchase_transactions"
    order_id: Mapped[UUID] = mapped_column(ForeignKey("payment_orders.id"), index=True)
    provider_id: Mapped[int] = mapped_column(ForeignKey("payment_providers.id"))
    environment: Mapped[str] = mapped_column(String(16))
    external_transaction_id: Mapped[str] = mapped_column(String(255))
    verification_status: Mapped[str] = mapped_column(String(16))
    verified_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    refunded_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    __table_args__ = (UniqueConstraint("provider_id", "environment", "external_transaction_id"),
                      ForeignKeyConstraint(["order_id", "provider_id", "environment"], ["payment_orders.id", "payment_orders.provider_id", "payment_orders.environment"], name="fk_payment_transaction_order_provider"),
                      CheckConstraint("environment IN ('TEST','PRODUCTION')", name="environment"),
                      CheckConstraint("verification_status IN ('PENDING','VERIFIED','REJECTED','REFUNDED')", name="status"))


class EntitlementGrant(IdentityMixin, CreatedAtMixin, Base):
    __tablename__ = "entitlement_grants"
    transaction_id: Mapped[UUID] = mapped_column(ForeignKey("purchase_transactions.id"))
    reward_line_index: Mapped[int] = mapped_column(Integer)
    reward_type: Mapped[str] = mapped_column(String(32))
    reward_ref: Mapped[str] = mapped_column(String(128))
    amount: Mapped[int] = mapped_column(BigInteger)
    revoked_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    __table_args__ = (UniqueConstraint("transaction_id", "reward_line_index"),
                      CheckConstraint("reward_line_index >= 0", name="line_nonnegative"),
                      CheckConstraint("amount > 0", name="amount_positive"))
