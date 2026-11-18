from datetime import datetime, timezone
from sqlalchemy import func, select
from ..tables import OutboxMessage
from ..tables.rating_broker import RatingResultMessage
from ....domain.rating import RATING_EVENT, validate_rating_receipt
from ....domain.rating_broker import command_hash


class RatingResultsRepository:
    def __init__(self, session):
        self.session = session

    def _lock(self, identity):
        self.session.execute(select(func.pg_advisory_xact_lock(
            func.hashtextextended('game.rating.result:' + str(identity), 0))))

    def quarantine(self, identity, digest, code):
        self._lock(identity)
        if self.session.get(RatingResultMessage, identity) is None:
            self.session.add(RatingResultMessage(event_id=identity, payload_hash=digest,
                                                outcome='QUARANTINED', code=code))

    def accept(self, result):
        self._lock(result.event_id)
        seen = self.session.get(RatingResultMessage, result.event_id)
        if seen is not None:
            return None if seen.payload_hash == result.digest else 'ResultConflict'
        row = self.session.get(OutboxMessage, result.message_id, with_for_update=True)
        if row is None or row.destination != 'rating' or row.event_name != RATING_EVENT or row.schema_version != 1:
            return 'UnknownSource'
        if result.command_hash != command_hash({'id': row.id, 'event_name': row.event_name,
                                                'schema_version': row.schema_version, 'payload': row.payload}):
            return 'SourceConflict'
        if result.status == 'APPLIED':
            try:
                if set(result.receipt) != set(row.payload) | {'boardId', 'scoreAfter'}:
                    raise ValueError()
                validate_rating_receipt(row.payload, result.receipt)
            except (ValueError, TypeError, KeyError, AttributeError):
                return 'ReceiptConflict'
        outcome = result.status
        if row.published_at is not None:
            if result.status == 'APPLIED' and row.receipt_json != result.receipt:
                return 'ReceiptConflict'
            outcome = 'IGNORED'
        elif result.status == 'APPLIED':
            row.published_at = datetime.now(timezone.utc)
            row.receipt_json = result.receipt
            row.failed_at = row.lease_token = row.last_error = None
        elif result.generation < row.broker_result_generation:
            outcome = 'IGNORED'
        elif result.generation == row.broker_result_generation:
            return 'GenerationConflict'
        else:
            row.failed_at = datetime.now(timezone.utc)
            row.lease_token = None
            row.last_error = 'Rating' + result.status.title()
        row.broker_result_generation = max(row.broker_result_generation, result.generation)
        self.session.add(RatingResultMessage(event_id=result.event_id, payload_hash=result.digest,
            message_id=result.message_id, generation=result.generation, outcome=outcome, code=result.code))
        return None
