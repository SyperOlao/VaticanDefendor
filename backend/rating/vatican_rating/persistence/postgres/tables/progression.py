from datetime import datetime
from uuid import UUID
from sqlalchemy import BigInteger, CheckConstraint, DateTime, ForeignKey, ForeignKeyConstraint, Index, Integer, String, UniqueConstraint, func, text
from sqlalchemy.orm import Mapped, mapped_column
from ..base import Base, CreatedAtMixin, IdentityMixin


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
