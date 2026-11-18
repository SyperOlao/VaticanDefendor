from .ports import UnitOfWorkFactory
from .errors import ApplicationError
from .run_scores import RunScoreConsumer
from .models import RankedEntry
import re
from uuid import UUID


def validate_code(code):
    if not isinstance(code, str) or re.fullmatch(r'[A-Za-z0-9_.-]{1,64}', code) is None:
        raise ApplicationError(422, 'Invalid leaderboard code')


def validate_range(limit, offset):
    if type(limit) is not int or not 1 <= limit <= 100 or type(offset) is not int or not 0 <= offset <= 100000:
        raise ApplicationError(422, 'Invalid ranking range')


def entry_response(entry: RankedEntry) -> dict:
    return {'accountId': str(entry.account_id), 'score': entry.score, 'position': entry.position}


class RatingUseCases:
    def __init__(self, uow: UnitOfWorkFactory):
        self.uow = uow
        self.run_scores = RunScoreConsumer(uow)

    def ranking(self, code, limit=50, offset=0):
        validate_code(code)
        validate_range(limit, offset)
        with self.uow() as work:
            rows = work.repository.ranking(code, limit, offset)
            if rows is None:
                raise ApplicationError(404, "Leaderboard not found")
            return {"board": code, "entries": [entry_response(row) for row in rows]}

    def boards(self, limit=50, offset=0):
        validate_range(limit, offset)
        with self.uow() as work:
            return {'boards': [{'code': board.code, 'scoreSource': board.score_source}
                               for board in work.repository.boards(limit, offset)]}

    def standing(self, code, account_id):
        validate_code(code)
        if not isinstance(account_id, UUID):
            raise ApplicationError(422, 'Invalid player identifier')
        with self.uow() as work:
            standing = work.repository.standing(code, account_id)
            if standing is None:
                raise ApplicationError(404, 'Leaderboard not found')
            return {'board': code, 'entry': entry_response(standing.entry) if standing.entry else None}

    def receive(self, message_id, event_name, schema_version, payload):
        return self.run_scores.receive(message_id,event_name,schema_version,payload)

    def configure_board(self, code):
        validate_code(code)
        with self.uow() as work:
            if not work.repository.configure_board(code):
                raise ApplicationError(409,'Leaderboard source is immutable; use a new code')
