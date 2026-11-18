from uuid import UUID, uuid5, NAMESPACE_URL
from sqlalchemy import and_, or_, select, func
from sqlalchemy.orm import aliased
from sqlalchemy.dialects.postgresql import insert
from ....application.catalog import MAX_SCORE, ScoreSource
from ....application.errors import ApplicationError
from ....application.models import BoardDefinition, PlayerStanding, RankedEntry
from ..tables import RunScoreReceipt
from .messaging import MessagingRepository
from ..tables import Leaderboard, LeaderboardScore


class StateRepository(MessagingRepository):
    def boards(self, limit, offset):
        rows = self.session.execute(select(Leaderboard.code, Leaderboard.score_source)
            .order_by(Leaderboard.code).offset(offset).limit(limit))
        return [BoardDefinition(row.code, row.score_source) for row in rows]

    def ranking(self, code, limit, offset):
        board = self.session.scalar(select(Leaderboard).where(Leaderboard.code == code))
        if board is None:
            return None
        rows = self.session.scalars(select(LeaderboardScore).where(LeaderboardScore.board_id == board.id)
            .order_by(LeaderboardScore.score.desc(), LeaderboardScore.account_id).offset(offset).limit(limit))
        return [RankedEntry(row.account_id, row.score, offset + i + 1) for i, row in enumerate(rows)]

    def standing(self, code, account_id):
        own, ahead = aliased(LeaderboardScore), aliased(LeaderboardScore)
        preceding = (select(func.count()).select_from(ahead)
            .where(ahead.board_id == Leaderboard.id,
                   or_(ahead.score > own.score,
                       and_(ahead.score == own.score, ahead.account_id < own.account_id)))
            .correlate(Leaderboard, own).scalar_subquery())
        # One statement gives the score and its position from the same MVCC snapshot.
        # A missing board and an existing board without this player remain distinct.
        row = self.session.execute(select(own.account_id, own.score, (preceding + 1).label('position'))
            .select_from(Leaderboard).outerjoin(own,
                and_(own.board_id == Leaderboard.id, own.account_id == account_id))
            .where(Leaderboard.code == code)).one_or_none()
        if row is None:
            return None
        return PlayerStanding(RankedEntry(row.account_id, row.score, row.position) if row.account_id else None)

    def configure_board(self, code):
        self.session.execute(select(func.pg_advisory_xact_lock(func.hashtextextended('rating.board:'+code,0))))
        board = self.session.scalar(select(Leaderboard).where(Leaderboard.code==code))
        if board:
            return board.score_source==ScoreSource.RUN_XP
        self.session.add(Leaderboard(id=uuid5(NAMESPACE_URL,'vaticandefendor:rating:'+code),code=code,score_source=ScoreSource.RUN_XP))
        return True

    def run_receipt(self, run_id, account_id, digest):
        row = self.session.get(RunScoreReceipt,run_id)
        if row is None:
            return None
        if row.account_id != account_id or row.payload_hash != digest:
            raise ApplicationError(409,'Run rating source payload changed')
        return row.receipt_json

    def add_run_score(self, account_id, code, points):
        board = self.session.scalar(select(Leaderboard).where(Leaderboard.code==code))
        if board is None or board.score_source != ScoreSource.RUN_XP:
            raise ApplicationError(422,'Leaderboard is unavailable or has an incompatible score source')
        # Let PostgreSQL add to the locked current value, including updates from
        # older writers. Reading then assigning a Python sum can lose an increment.
        statement = insert(LeaderboardScore).values(board_id=board.id, account_id=account_id, score=points)
        statement = statement.on_conflict_do_update(
            index_elements=[LeaderboardScore.board_id, LeaderboardScore.account_id],
            set_={'score': LeaderboardScore.score + points, 'updated_at': func.now()},
            where=LeaderboardScore.score <= MAX_SCORE - points,
        ).returning(LeaderboardScore.score)
        after = self.session.scalar(statement)
        if after is None:
            raise ApplicationError(409,'Leaderboard score capacity exceeded')
        return {'boardId':str(board.id),'scoreAfter':after}

    def record_run_receipt(self, run_id, account_id, ruleset_id, content_release_id, points, digest, receipt):
        self.session.add(RunScoreReceipt(run_id=run_id,account_id=account_id,board_id=UUID(receipt['boardId']),
            ruleset_id=ruleset_id,content_release_id=content_release_id,points=points,payload_hash=digest,receipt_json=receipt))
