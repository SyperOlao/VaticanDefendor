from datetime import datetime
from uuid import UUID

from sqlalchemy import BigInteger, CheckConstraint, DateTime, ForeignKey, ForeignKeyConstraint, Index, Integer, String, Text, UniqueConstraint, func, text
from sqlalchemy.dialects.postgresql import JSONB
from sqlalchemy.orm import Mapped, mapped_column

from ..base import Base, CreatedAtMixin, IdentityMixin


class GameServer(IdentityMixin, Base):
    __tablename__ = "game_servers"
    host: Mapped[str] = mapped_column(String(255))
    port: Mapped[int] = mapped_column(Integer)
    content_release_id: Mapped[str] = mapped_column(String(128))
    protocol_version: Mapped[int] = mapped_column(Integer)
    generation: Mapped[int] = mapped_column(Integer, server_default="0")
    current_run_id: Mapped[UUID | None] = mapped_column(
        ForeignKey("runs.id", name="game_servers_current_run_fk", use_alter=True))
    status: Mapped[str] = mapped_column(String(16))
    last_seen: Mapped[datetime] = mapped_column(DateTime(timezone=True))
    __table_args__ = (CheckConstraint("port BETWEEN 1 AND 65535", name="port_range"),
                      CheckConstraint("status IN ('ready', 'assigned', 'occupied', 'unavailable')", name="status"))


class Run(IdentityMixin, CreatedAtMixin, Base):
    __tablename__ = "runs"
    account_id: Mapped[UUID] = mapped_column()
    server_id: Mapped[UUID | None] = mapped_column(ForeignKey("game_servers.id"))
    simulation_mode: Mapped[str] = mapped_column(String(16), server_default="DEDICATED")
    ruleset_id: Mapped[str] = mapped_column(ForeignKey("rulesets.id"))
    content_release_id: Mapped[str] = mapped_column(String(128))
    profile_version: Mapped[int] = mapped_column(Integer)
    profile_display_name: Mapped[str] = mapped_column(String(32))
    generation: Mapped[int] = mapped_column(Integer)
    status: Mapped[str] = mapped_column(String(16))
    completed_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    outcome: Mapped[str | None] = mapped_column(String(16))
    permanent_reward: Mapped[int] = mapped_column(BigInteger, server_default="0")
    reward_per_church_level: Mapped[int | None] = mapped_column(BigInteger)
    weapon_snapshot: Mapped[dict | None] = mapped_column(JSONB)
    __table_args__ = (
        UniqueConstraint("id", "account_id"),
        UniqueConstraint("id", "ruleset_id"),
        CheckConstraint("(simulation_mode = 'DEDICATED' AND server_id IS NOT NULL) OR (simulation_mode = 'LOCAL' AND server_id IS NULL)", name="simulation_mode"),
        CheckConstraint("status IN ('pending', 'ready', 'failed', 'completed')", name="status"),
        CheckConstraint("permanent_reward >= 0", name="reward_nonnegative"),
        CheckConstraint("reward_per_church_level >= 0", name="reward_rate_nonnegative"),
        Index("runs_account_idx", "account_id", text("created_at DESC")),
        Index("runs_live_account_idx", "account_id", unique=True, postgresql_where=text("status IN ('pending', 'ready')")),
        Index("runs_live_server_idx", "server_id", unique=True, postgresql_where=text("status IN ('pending', 'ready')")),
    )


class RunResultOperation(Base):
    __tablename__ = "run_result_operations"
    run_id: Mapped[UUID] = mapped_column(ForeignKey("runs.id"), primary_key=True)
    account_id: Mapped[UUID] = mapped_column()
    server_id: Mapped[UUID] = mapped_column(ForeignKey("game_servers.id"))
    generation: Mapped[int] = mapped_column(Integer)
    content_release_id: Mapped[str] = mapped_column(String(128))
    payload_hash: Mapped[bytes]
    outcome: Mapped[str] = mapped_column(String(16))
    permanent_reward: Mapped[int] = mapped_column(BigInteger)
    profile_version: Mapped[int] = mapped_column(Integer)
    church_level: Mapped[int] = mapped_column(Integer, server_default="1")
    applied_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())
    __table_args__ = (CheckConstraint("permanent_reward >= 0", name="reward_nonnegative"),
                      CheckConstraint("profile_version > 0", name="version_positive"),
                      CheckConstraint("church_level BETWEEN 1 AND 1000", name="church_level_range"))


class RunCheckpoint(CreatedAtMixin, Base):
    __tablename__ = "run_checkpoints"
    run_id: Mapped[UUID] = mapped_column(ForeignKey("runs.id"), primary_key=True)
    generation: Mapped[int] = mapped_column(Integer)
    schema_version: Mapped[int] = mapped_column(Integer)
    simulation_tick: Mapped[int] = mapped_column(BigInteger)
    snapshot_json: Mapped[str] = mapped_column(Text)
    sha256: Mapped[bytes]
    __table_args__ = (CheckConstraint("generation > 0", name="generation_positive"),
                      CheckConstraint("schema_version > 0", name="schema_positive"),
                      CheckConstraint("simulation_tick >= 0", name="tick_nonnegative"),
                      CheckConstraint("octet_length(sha256) = 32", name="sha256_length"))


class RunContext(Base):
    """Versioned start parameters. Existing runs remain readable without a context."""
    __tablename__ = "run_contexts"
    run_id: Mapped[UUID] = mapped_column(ForeignKey("runs.id"), primary_key=True)
    account_id: Mapped[UUID] = mapped_column()
    session_id: Mapped[UUID]
    client_request_key: Mapped[UUID]
    map_id: Mapped[str] = mapped_column(String(128))
    difficulty: Mapped[str] = mapped_column(String(32))
    ruleset_id: Mapped[str] = mapped_column(ForeignKey("rulesets.id"))
    energy_cost: Mapped[int] = mapped_column(Integer)
    started_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())
    submission_deadline: Mapped[datetime] = mapped_column(DateTime(timezone=True))
    __table_args__ = (ForeignKeyConstraint(["run_id", "account_id"], ["runs.id", "runs.account_id"], name="fk_run_context_owner"),
                      ForeignKeyConstraint(["run_id", "ruleset_id"], ["runs.id", "runs.ruleset_id"], name="fk_run_context_ruleset"),
                      UniqueConstraint("account_id", "client_request_key"),
                      CheckConstraint("energy_cost >= 0", name="energy_nonnegative"),
                      CheckConstraint("submission_deadline > started_at", name="deadline_after_start"))


class RunResultSubmission(CreatedAtMixin, Base):
    __tablename__ = "run_result_submissions"
    run_id: Mapped[UUID] = mapped_column(ForeignKey("runs.id"), primary_key=True)
    payload_hash: Mapped[bytes]
    schema_version: Mapped[int] = mapped_column(Integer)
    snapshot: Mapped[dict] = mapped_column(JSONB)
    validation_status: Mapped[str] = mapped_column(String(16))
    validator_version: Mapped[str] = mapped_column(String(64))
    validation_reasons: Mapped[list] = mapped_column(JSONB, server_default="[]")
    settlement: Mapped[dict | None] = mapped_column(JSONB)
    __table_args__ = (CheckConstraint("octet_length(payload_hash) = 32", name="hash_length"),
                      CheckConstraint("schema_version > 0", name="schema_positive"),
                      CheckConstraint("validation_status IN ('ACCEPTED','REVIEW','REJECTED')", name="validation_status"),
                      CheckConstraint("validation_status = 'ACCEPTED' OR settlement IS NULL", name="accepted_settlement"))


class RunValidationAudit(IdentityMixin, CreatedAtMixin, Base):
    __tablename__ = "run_validation_audits"
    run_id: Mapped[UUID] = mapped_column(ForeignKey("run_result_submissions.run_id"), index=True)
    ruleset_id: Mapped[str] = mapped_column(ForeignKey("rulesets.id"))
    validator_version: Mapped[str] = mapped_column(String(64))
    status: Mapped[str] = mapped_column(String(16))
    reasons: Mapped[list] = mapped_column(JSONB, server_default="[]")
    __table_args__ = (CheckConstraint("status IN ('ACCEPTED','REVIEW','REJECTED')", name="status"),)
