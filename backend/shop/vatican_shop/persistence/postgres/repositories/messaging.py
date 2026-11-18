from sqlalchemy import select, func
from .analytics import AnalyticsRepository
from ...postgres.tables import ConsumedMessage
from ....application.errors import ApplicationError


class MessagingRepository(AnalyticsRepository):
    def __init__(self, session):
        self.session = session

    def lock_player(self, account_id):
        # Transaction-scoped lock also serializes the first creation of a player's state.
        self.session.execute(select(func.pg_advisory_xact_lock(int.from_bytes(account_id.bytes[:8], "big", signed=True))))

    def received(self, message_id, payload_hash):
        row = self.session.get(ConsumedMessage, ("rewards.v1", message_id))
        if row is None:
            return False
        if row.payload_hash is not None and row.payload_hash != payload_hash:
            raise ApplicationError(409, "Message payload changed")
        return True

    def record_message(self, message_id, payload_hash):
        self.session.add(ConsumedMessage(consumer="rewards.v1", message_id=message_id, payload_hash=payload_hash))
