from uuid import uuid5
from sqlalchemy import select, func
from ...postgres.tables import ConsumedMessage
from ....application.errors import ApplicationError
from ....application.catalog import MESSAGE_CONSUMER


class MessagingRepository:
    def __init__(self, session):
        self.session = session

    def lock_player(self, account_id):
        # Keep the existing lock key compatible with already running rating replicas.
        self.session.execute(select(func.pg_advisory_xact_lock(
            int.from_bytes(account_id.bytes[:8], 'big', signed=True))))

    def lock_message(self, message_id):
        self._lock('rating.message:' + str(message_id))

    def lock_run(self, run_id):
        self.lock_player(uuid5(run_id, 'rating.run.xp'))

    def _lock(self, key):
        # All consumers acquire message, source, then player locks in that order.
        self.session.execute(select(func.pg_advisory_xact_lock(func.hashtextextended(key, 0))))

    def received(self, message_id, payload_hash):
        row = self.session.get(ConsumedMessage, (MESSAGE_CONSUMER, message_id))
        if row is None:
            return False
        if row.payload_hash is not None and row.payload_hash != payload_hash:
            raise ApplicationError(409, "Message payload changed")
        return True

    def record_message(self, message_id, payload_hash):
        self.session.add(ConsumedMessage(consumer=MESSAGE_CONSUMER, message_id=message_id, payload_hash=payload_hash))
