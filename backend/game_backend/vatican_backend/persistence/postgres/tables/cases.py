from uuid import UUID

from sqlalchemy import BigInteger, CheckConstraint, ForeignKey, Integer, String, UniqueConstraint
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
    price_currency: Mapped[str] = mapped_column(ForeignKey("currencies.code"))
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
    reward_bundle_id: Mapped[UUID] = mapped_column(ForeignKey("reward_bundles.id"))
    rarity: Mapped[str] = mapped_column(String(32))
    pity_before: Mapped[int] = mapped_column(Integer)
    pity_after: Mapped[int] = mapped_column(Integer)
    result: Mapped[dict] = mapped_column(JSONB)
    __table_args__ = (UniqueConstraint("account_id", "client_request_key"),
                      CheckConstraint("pity_before >= 0 AND pity_after >= 0", name="pity_nonnegative"))
