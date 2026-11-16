from datetime import datetime
from uuid import UUID

from sqlalchemy import BigInteger, CheckConstraint, DateTime, ForeignKey, ForeignKeyConstraint, Index, Integer, String, UniqueConstraint, func, text
from sqlalchemy.orm import Mapped, mapped_column

from ..base import Base, CreatedAtMixin, IdentityMixin


class RewardBundle(IdentityMixin, Base):
    __tablename__ = "reward_bundles"
    code: Mapped[str] = mapped_column(String(64))
    version: Mapped[int] = mapped_column(Integer)
    __table_args__ = (UniqueConstraint("code", "version"), CheckConstraint("version > 0", name="version_positive"))


class RewardBundleItem(Base):
    __tablename__ = "reward_bundle_items"
    bundle_id: Mapped[UUID] = mapped_column(ForeignKey("reward_bundles.id"), primary_key=True)
    item_index: Mapped[int] = mapped_column(Integer, primary_key=True)
    reward_type: Mapped[str] = mapped_column(String(32))
    reward_ref: Mapped[str] = mapped_column(String(128))
    quantity: Mapped[int] = mapped_column(BigInteger)
    __table_args__ = (CheckConstraint("item_index >= 0", name="index_nonnegative"),
                      CheckConstraint("quantity > 0", name="quantity_positive"))


class PlayerLevelDefinition(Base):
    __tablename__ = "player_level_definitions"
    ruleset_id: Mapped[str] = mapped_column(ForeignKey("rulesets.id"), primary_key=True)
    level: Mapped[int] = mapped_column(Integer, primary_key=True)
    total_xp_required: Mapped[int] = mapped_column(BigInteger)
    reward_bundle_id: Mapped[UUID | None] = mapped_column(ForeignKey("reward_bundles.id"))
    __table_args__ = (UniqueConstraint("ruleset_id", "total_xp_required"),
                      UniqueConstraint("ruleset_id", "level", "reward_bundle_id", name="uq_level_definition_reward"),
                      CheckConstraint("level > 0", name="level_positive"),
                      CheckConstraint("total_xp_required >= 0", name="xp_nonnegative"))


class PlayerLevelProgress(Base):
    __tablename__ = "player_level_progress"
    account_id: Mapped[UUID] = mapped_column(primary_key=True)
    total_xp: Mapped[int] = mapped_column(BigInteger, server_default="0")
    current_level: Mapped[int] = mapped_column(Integer, server_default="1")
    __table_args__ = (CheckConstraint("total_xp >= 0", name="xp_nonnegative"),
                      CheckConstraint("current_level > 0", name="level_positive"))


class XPRewardRule(IdentityMixin, Base):
    __tablename__ = "xp_reward_rules"
    ruleset_id: Mapped[str] = mapped_column(ForeignKey("rulesets.id"))
    source_type: Mapped[str] = mapped_column(String(32))
    target_key: Mapped[str] = mapped_column(String(128))
    xp_per_unit: Mapped[int] = mapped_column(Integer)
    max_xp_per_run: Mapped[int] = mapped_column(BigInteger)
    __table_args__ = (UniqueConstraint("ruleset_id", "source_type", "target_key"),
                      CheckConstraint("source_type IN ('HERO_KILL','TOWER_KILL','BUILDING_UPGRADE','RUN_VICTORY')", name="source_type"),
                      CheckConstraint("xp_per_unit >= 0 AND max_xp_per_run >= 0", name="xp_nonnegative"))


class RunXPBreakdown(Base):
    __tablename__ = "run_xp_breakdowns"
    run_id: Mapped[UUID] = mapped_column(ForeignKey("runs.id"), primary_key=True)
    ruleset_id: Mapped[str] = mapped_column(ForeignKey("rulesets.id"))
    hero_kill_xp: Mapped[int] = mapped_column(BigInteger)
    tower_kill_xp: Mapped[int] = mapped_column(BigInteger)
    tycoon_xp: Mapped[int] = mapped_column(BigInteger)
    victory_xp: Mapped[int] = mapped_column(BigInteger)
    total_xp: Mapped[int] = mapped_column(BigInteger)
    __table_args__ = (CheckConstraint("hero_kill_xp >= 0 AND tower_kill_xp >= 0 AND tycoon_xp >= 0 AND victory_xp >= 0", name="xp_nonnegative"),
                      ForeignKeyConstraint(["run_id", "ruleset_id"], ["runs.id", "runs.ruleset_id"], name="fk_run_xp_ruleset"),
                      CheckConstraint("total_xp = hero_kill_xp + tower_kill_xp + tycoon_xp + victory_xp", name="xp_total"))


class PlayerXPTransaction(IdentityMixin, CreatedAtMixin, Base):
    __tablename__ = "player_xp_transactions"
    account_id: Mapped[UUID] = mapped_column(index=True)
    run_id: Mapped[UUID] = mapped_column(ForeignKey("run_xp_breakdowns.run_id"), unique=True)
    xp_delta: Mapped[int] = mapped_column(BigInteger)
    __table_args__ = (CheckConstraint("xp_delta >= 0", name="xp_nonnegative"),
                      ForeignKeyConstraint(["run_id", "account_id"], ["runs.id", "runs.account_id"], name="fk_xp_transaction_run_owner"))


class PlayerLevelRewardClaim(IdentityMixin, Base):
    __tablename__ = "player_level_reward_claims"
    account_id: Mapped[UUID] = mapped_column()
    rewarded_level: Mapped[int] = mapped_column(Integer)
    ruleset_id: Mapped[str] = mapped_column(ForeignKey("rulesets.id"))
    reward_bundle_id: Mapped[UUID] = mapped_column(ForeignKey("reward_bundles.id"))
    status: Mapped[str] = mapped_column(String(16), server_default="AVAILABLE")
    claimed_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    __table_args__ = (UniqueConstraint("account_id", "rewarded_level"),
                      ForeignKeyConstraint(["ruleset_id", "rewarded_level", "reward_bundle_id"],
                          ["player_level_definitions.ruleset_id", "player_level_definitions.level", "player_level_definitions.reward_bundle_id"],
                          name="fk_level_claim_definition_reward"),
                      UniqueConstraint("id", "reward_bundle_id"),
                      CheckConstraint("rewarded_level > 0", name="level_positive"),
                      CheckConstraint("(status = 'AVAILABLE' AND claimed_at IS NULL) OR (status = 'CLAIMED' AND claimed_at IS NOT NULL)", name="claim_state"))


class LevelRewardGrant(CreatedAtMixin, Base):
    __tablename__ = "level_reward_grants"
    claim_id: Mapped[UUID] = mapped_column(primary_key=True)
    item_index: Mapped[int] = mapped_column(Integer, primary_key=True)
    bundle_id: Mapped[UUID]
    __table_args__ = (
        ForeignKeyConstraint(["claim_id", "bundle_id"], ["player_level_reward_claims.id", "player_level_reward_claims.reward_bundle_id"]),
        ForeignKeyConstraint(["bundle_id", "item_index"], ["reward_bundle_items.bundle_id", "reward_bundle_items.item_index"]),
    )


class Leaderboard(IdentityMixin, Base):
    __tablename__ = "leaderboards"
    code: Mapped[str] = mapped_column(String(64), unique=True)
    score_source: Mapped[str] = mapped_column(String(64))


class LeaderboardScore(Base):
    __tablename__ = "leaderboard_scores"
    board_id: Mapped[UUID] = mapped_column(ForeignKey("leaderboards.id"), primary_key=True)
    account_id: Mapped[UUID] = mapped_column(primary_key=True)
    score: Mapped[int] = mapped_column(BigInteger)
    updated_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())
    __table_args__ = (CheckConstraint("score >= 0", name="score_nonnegative"),
                      Index("ix_leaderboard_scores_ranking", "board_id", text("score DESC"), "account_id"))


class PrestigeRewardRule(Base):
    __tablename__ = "prestige_reward_rules"
    ruleset_id: Mapped[str] = mapped_column(ForeignKey("rulesets.id"), primary_key=True)
    church_level: Mapped[int] = mapped_column(Integer, primary_key=True)
    reward: Mapped[int] = mapped_column(BigInteger)
    __table_args__ = (CheckConstraint("church_level > 0", name="level_positive"),
                      CheckConstraint("reward >= 0", name="reward_nonnegative"))


class RunRewardGrant(CreatedAtMixin, Base):
    __tablename__ = "run_reward_grants"
    run_id: Mapped[UUID] = mapped_column(ForeignKey("runs.id"), primary_key=True)
    account_id: Mapped[UUID] = mapped_column()
    transaction_id: Mapped[UUID] = mapped_column(ForeignKey("economy_transactions.id"), unique=True)
    ruleset_id: Mapped[str]
    church_level: Mapped[int] = mapped_column(Integer)
    __table_args__ = (ForeignKeyConstraint(["ruleset_id", "church_level"],
                                          ["prestige_reward_rules.ruleset_id", "prestige_reward_rules.church_level"]),
                      ForeignKeyConstraint(["run_id", "ruleset_id"], ["runs.id", "runs.ruleset_id"], name="fk_run_reward_ruleset"),
                      ForeignKeyConstraint(["run_id", "account_id"], ["runs.id", "runs.account_id"], name="fk_run_reward_owner"),
                      ForeignKeyConstraint(["transaction_id", "account_id"], ["economy_transactions.id", "economy_transactions.account_id"], name="fk_run_reward_ledger_owner"))
