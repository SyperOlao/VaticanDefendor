from datetime import datetime
from uuid import UUID

from sqlalchemy import BigInteger, LargeBinary, CheckConstraint, ForeignKey, ForeignKeyConstraint, DateTime, Index, Integer, String, UniqueConstraint, text
from sqlalchemy.dialects.postgresql import JSONB
from sqlalchemy.orm import Mapped, mapped_column

from ..base import Base, CreatedAtMixin, IdentityMixin


class CaseType(Base):
    __tablename__ = "case_types"
    code: Mapped[str] = mapped_column(String(64), primary_key=True)


class CaseDefinition(IdentityMixin, Base):
    __tablename__ = "case_definitions"
    code: Mapped[str] = mapped_column(ForeignKey("case_types.code"))
    ruleset_id: Mapped[str] = mapped_column(ForeignKey("rulesets.id"))
    price_currency: Mapped[str] = mapped_column(String(32))
    price: Mapped[int] = mapped_column(BigInteger)
    pity_threshold: Mapped[int] = mapped_column(Integer)
    guaranteed_rarity: Mapped[str] = mapped_column(String(32))
    __table_args__ = (UniqueConstraint("code", "ruleset_id"),
                      CheckConstraint("price >= 0 AND pity_threshold > 0", name="price_and_pity"))


class CaseDropEntry(Base):
    __tablename__ = "case_drop_entries"
    case_id: Mapped[UUID] = mapped_column(ForeignKey("case_definitions.id"), primary_key=True)
    entry_index: Mapped[int] = mapped_column(Integer, primary_key=True)
    reward_bundle_id: Mapped[UUID] = mapped_column(ForeignKey("reward_bundles.id"))
    rarity: Mapped[str] = mapped_column(String(32))
    weight: Mapped[int] = mapped_column(BigInteger)
    __table_args__ = (CheckConstraint("entry_index >= 0 AND weight > 0", name="entry_weight"),)


class PlayerCasePity(Base):
    __tablename__ = "player_case_pity"
    account_id: Mapped[UUID] = mapped_column(primary_key=True)
    case_code: Mapped[str] = mapped_column(ForeignKey("case_types.code"), primary_key=True)
    rolls_since_guarantee: Mapped[int] = mapped_column(Integer, server_default="0")
    version: Mapped[int] = mapped_column(BigInteger, server_default="1")
    __table_args__ = (CheckConstraint("rolls_since_guarantee >= 0 AND version > 0", name="counters"),)


class CaseOpening(IdentityMixin, CreatedAtMixin, Base):
    __tablename__ = "case_openings"
    account_id: Mapped[UUID] = mapped_column()
    case_id: Mapped[UUID] = mapped_column(ForeignKey("case_definitions.id"))
    client_request_key: Mapped[UUID]
    reward_bundle_id: Mapped[UUID | None] = mapped_column(ForeignKey("reward_bundles.id"))
    rarity: Mapped[str | None] = mapped_column(String(32))
    pity_before: Mapped[int | None] = mapped_column(Integer)
    pity_after: Mapped[int | None] = mapped_column(Integer)
    result: Mapped[dict] = mapped_column(JSONB)
    case_code: Mapped[str] = mapped_column(ForeignKey("case_types.code"))
    status: Mapped[str] = mapped_column(String(16), server_default="COMPLETED")
    payment_mode: Mapped[str | None] = mapped_column(String(16))
    definition_snapshot: Mapped[dict | None] = mapped_column(JSONB)
    random_seed: Mapped[bytes | None] = mapped_column(LargeBinary)
    payment_message_id: Mapped[UUID | None] = mapped_column(ForeignKey("outbox_messages.id"), unique=True)
    payment_receipt: Mapped[dict | None] = mapped_column(JSONB)
    completed_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    __table_args__ = (UniqueConstraint("account_id", "client_request_key"),
                      CheckConstraint("pity_before >= 0 AND pity_after >= 0", name="pity_nonnegative"),
                      UniqueConstraint("id", "reward_bundle_id"),
                      CheckConstraint("random_seed IS NULL OR octet_length(random_seed) = 32", name="seed_length"),
                      CheckConstraint("status IN ('FUNDING','PENDING','REVIEW','COMPLETED','REJECTED')", name="opening_status"),
                      CheckConstraint("payment_mode IS NULL OR payment_mode IN ('INVENTORY','WALLET')", name="payment_mode"),
                      Index("uq_case_opening_active", "account_id", "case_code", unique=True,
                            postgresql_where=text("status IN ('FUNDING','PENDING','REVIEW')")))


class CaseRewardGrant(CreatedAtMixin, Base):
    __tablename__ = "case_reward_grants"
    opening_id: Mapped[UUID] = mapped_column(primary_key=True)
    item_index: Mapped[int] = mapped_column(Integer, primary_key=True)
    bundle_id: Mapped[UUID]
    reward_type: Mapped[str] = mapped_column(String(32))
    reward_ref: Mapped[str] = mapped_column(String(128))
    quantity: Mapped[int] = mapped_column(BigInteger)
    effect_config: Mapped[dict | None] = mapped_column(JSONB(none_as_null=True))
    status: Mapped[str] = mapped_column(String(16))
    message_id: Mapped[UUID | None] = mapped_column(ForeignKey("outbox_messages.id"), unique=True)
    receipt_json: Mapped[dict | None] = mapped_column(JSONB)
    completed_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    __table_args__ = (
        ForeignKeyConstraint(["opening_id", "bundle_id"], ["case_openings.id", "case_openings.reward_bundle_id"]),
        ForeignKeyConstraint(["bundle_id"], ["reward_bundles.id"]),
        CheckConstraint("item_index >= 0 AND quantity > 0", name="line_values"),
        CheckConstraint("status IN ('PENDING','COMPLETED','FAILED')", name="delivery_status"),
    )
