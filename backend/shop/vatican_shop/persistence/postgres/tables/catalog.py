from datetime import datetime
from uuid import UUID

from sqlalchemy import BigInteger, Boolean, CheckConstraint, DateTime, ForeignKey, Index, Integer, String, func
from sqlalchemy.dialects.postgresql import JSONB
from sqlalchemy.orm import Mapped, mapped_column

from ..base import Base, CreatedAtMixin, IdentityMixin


class ShopCatalogState(Base):
    __tablename__ = "shop_catalog_state"
    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    revision: Mapped[int] = mapped_column(BigInteger, server_default="1")
    __table_args__ = (CheckConstraint("id = 1 AND revision > 0", name="singleton_revision"),)


class ShopOffer(CreatedAtMixin, Base):
    __tablename__ = "shop_offers"
    id: Mapped[str] = mapped_column(String(64), primary_key=True)
    configuration_id: Mapped[str] = mapped_column(String(80))
    title: Mapped[str] = mapped_column(String(80))
    image_key: Mapped[str | None] = mapped_column(String(96))
    price: Mapped[int] = mapped_column(BigInteger)
    purchase_limit: Mapped[int] = mapped_column(BigInteger, server_default="0")
    available: Mapped[bool] = mapped_column(Boolean, server_default="true")
    unavailable_reason: Mapped[str] = mapped_column(String(64), server_default="")
    revision: Mapped[int] = mapped_column(Integer, server_default="1")
    updated_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())
    deleted_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    __table_args__ = (
        CheckConstraint("price >= 0 AND purchase_limit >= 0 AND revision > 0", name="values"),
        CheckConstraint("id ~ '^[a-z0-9_.-]{1,64}$' AND configuration_id ~ '^[A-Za-z0-9_.-]{1,80}$'", name="identifiers"),
        CheckConstraint("length(btrim(title)) > 0", name="title"),
        CheckConstraint("image_key IS NULL OR image_key ~ '^shop/items/[a-f0-9]{64}\\.(png|jpg)$'", name="image_key"),
        CheckConstraint("unavailable_reason = '' OR unavailable_reason ~ '^[A-Za-z][A-Za-z0-9]{0,63}$'", name="reason"),
        CheckConstraint("NOT available OR (unavailable_reason = '' AND deleted_at IS NULL)", name="availability"),
    )


class ShopCatalogAudit(IdentityMixin, CreatedAtMixin, Base):
    __tablename__ = "shop_catalog_audit"
    actor_account_id: Mapped[UUID]
    offer_id: Mapped[str] = mapped_column(ForeignKey("shop_offers.id"))
    action: Mapped[str] = mapped_column(String(32))
    before_json: Mapped[dict | None] = mapped_column(JSONB)
    after_json: Mapped[dict] = mapped_column(JSONB)
    __table_args__ = (
        CheckConstraint("action IN ('shop.offer.create','shop.offer.update','shop.offer.delete')", name="action"),
        Index("shop_catalog_audit_offer_idx", "offer_id", "created_at"),
    )
