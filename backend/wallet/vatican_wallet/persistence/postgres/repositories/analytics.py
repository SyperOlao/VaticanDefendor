from datetime import datetime, timedelta, timezone
from uuid import uuid4
from sqlalchemy import select, update

from ....domain.analytics import ECONOMY_EVENT, MAX_DELIVERY_ATTEMPTS, message_id, payload_for
from ..tables import OutboxMessage


def now():
    return datetime.now(timezone.utc)


class AnalyticsRepository:
    def record_transaction(self, transaction):
        self.session.add(transaction)
        self.session.add(OutboxMessage(id=message_id(transaction.id), destination="analytics",
            event_name=ECONOMY_EVENT, schema_version=1, aggregate_id=transaction.id, payload=payload_for(transaction)))

    def claim_analytics(self):
        row = self.session.scalar(select(OutboxMessage).where(OutboxMessage.event_name == ECONOMY_EVENT,
            OutboxMessage.destination == "analytics", OutboxMessage.published_at.is_(None),
            OutboxMessage.failed_at.is_(None), OutboxMessage.available_at <= now())
            .order_by(OutboxMessage.created_at, OutboxMessage.id).limit(1).with_for_update(skip_locked=True))
        if row is None:
            return None
        if row.attempts >= MAX_DELIVERY_ATTEMPTS:
            row.lease_token = None
            row.failed_at = now()
            row.last_error = "DeliveryUnconfirmed"
            return {"exhausted": True}
        row.lease_token = uuid4()
        row.attempts += 1
        row.available_at = now() + timedelta(seconds=30)
        return {"id": row.id, "event_name": row.event_name, "schema_version": row.schema_version,
                "payload": row.payload, "lease": row.lease_token}

    def confirm_analytics(self, identity, lease, receipt):
        changed = self.session.scalar(update(OutboxMessage).where(OutboxMessage.id == identity,
            OutboxMessage.lease_token == lease, OutboxMessage.published_at.is_(None),
            OutboxMessage.failed_at.is_(None), OutboxMessage.event_name == ECONOMY_EVENT)
            .values(published_at=now(), lease_token=None, last_error=None, receipt_json=receipt)
            .returning(OutboxMessage.id))
        return changed is not None

    def retry_analytics(self, identity, lease, permanent=False):
        row = self.session.get(OutboxMessage, identity, with_for_update=True)
        if row is None or row.event_name != ECONOMY_EVENT or row.lease_token != lease or row.published_at:
            return
        row.lease_token = None
        row.last_error = "DeliveryRejected" if permanent else "DeliveryUnavailable"
        if permanent or row.attempts >= MAX_DELIVERY_ATTEMPTS:
            row.failed_at = now()
        else:
            row.available_at = now() + timedelta(seconds=min(300, 2 ** min(row.attempts, 8)))

    def requeue_analytics(self, transaction_id):
        row = self.session.get(OutboxMessage, message_id(transaction_id), with_for_update=True)
        if row is None or row.event_name != ECONOMY_EVENT or row.published_at or not row.failed_at:
            return False
        row.failed_at = None
        row.lease_token = None
        row.attempts = 0
        row.last_error = None
        row.available_at = now()
        return True
