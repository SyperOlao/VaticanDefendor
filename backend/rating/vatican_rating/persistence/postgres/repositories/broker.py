from datetime import timedelta
from uuid import uuid4, uuid5

from sqlalchemy import func, select

from ..tables import BrokerInbox, BrokerRejection, OutboxMessage
from ....domain.broker import (BrokerCode, CommandJob, Destination, EventJob, InboxStatus,
                              LEASE_SECONDS, MAX_ATTEMPTS, result_id, retry_seconds)


def now():
    # Leases and retry times use the database clock shared by all worker processes.
    return func.clock_timestamp()


class BrokerRepository:
    def __init__(self, session):
        self.session = session

    def _lock(self, identity):
        identity = uuid5(identity, "rating.broker")
        self.session.execute(select(func.pg_advisory_xact_lock(int.from_bytes(identity.bytes[:8], "big", signed=True))))

    def accept(self, command):
        self._lock(command.message_id)
        row = self.session.get(BrokerInbox, command.message_id)
        if row is not None:
            if row.payload_hash != command.digest:
                return False
            if row.status != InboxStatus.PENDING:
                event = self.session.get(OutboxMessage, result_id(row.message_id, row.generation, Destination.RESULT),
                                         with_for_update=True)
                if event is not None and (event.published_at is not None or event.failed_at is not None):
                    event.published_at = event.failed_at = event.lease_token = event.last_error = None
                    event.attempts, event.available_at = 0, now()
            return True
        self.session.add(BrokerInbox(message_id=command.message_id, envelope=command.envelope, payload_hash=command.digest))
        return True

    def _event(self, identity, generation, destination, payload):
        self.session.add(OutboxMessage(id=result_id(identity, generation, destination), destination=destination,
            event_name="rating.command.result" if destination == Destination.RESULT else "rating.command.dead",
            schema_version=1, aggregate_id=identity, payload=payload))

    def reject_delivery(self, identity, body_hash, body_prefix, body_size, code, message_id=None):
        self._lock(identity)
        if self.session.get(BrokerRejection, identity) is not None:
            return
        self.session.add(BrokerRejection(id=identity, message_id=message_id, body_hash=body_hash,
            body_prefix=body_prefix, body_size=body_size, code=code))
        self._event(identity, 1, Destination.QUARANTINE, {"rejectionId": str(identity),
            "messageId": str(message_id) if message_id else None, "code": code,
            "bodySha256": body_hash.hex(), "bodyTruncated": body_size > 4096})

    def claim_command(self):
        row = self.session.scalar(select(BrokerInbox).where(BrokerInbox.status == InboxStatus.PENDING,
            BrokerInbox.available_at <= func.now()).order_by(BrokerInbox.available_at, BrokerInbox.created_at,
            BrokerInbox.message_id).limit(1).with_for_update(skip_locked=True))
        if row is None:
            return None
        row.lease_token = uuid4()
        row.available_at = now() + timedelta(seconds=LEASE_SECONDS)
        job = CommandJob(row.message_id, row.envelope, row.lease_token, row.attempts + 1, row.generation)
        if row.attempts >= MAX_ATTEMPTS:
            self.finish_command(job, InboxStatus.REVIEW, {"messageId": str(row.message_id),
                "status": InboxStatus.REVIEW, "code": row.last_error or BrokerCode.HANDLER_UNAVAILABLE},
                row.last_error or BrokerCode.HANDLER_UNAVAILABLE)
            return None
        row.attempts += 1
        return job

    def finish_command(self, job, status, result, code=None):
        row = self.session.get(BrokerInbox, job.message_id, with_for_update=True)
        if row is None or row.status != InboxStatus.PENDING or row.lease_token != job.lease or row.generation != job.generation:
            return False
        row.status, row.result_json = status, result
        row.finished_at, row.lease_token, row.last_error = now(), None, code
        payload = {**result, "deliveryGeneration": row.generation, "commandHash": row.payload_hash.hex()}
        self._event(row.message_id, row.generation, Destination.RESULT, payload)
        if status in (InboxStatus.REJECTED, InboxStatus.REVIEW):
            self._event(row.message_id, row.generation, Destination.DEAD, {
                "messageId": str(row.message_id), "deliveryGeneration": row.generation,
                "status": status, "code": code, "payloadSha256": row.payload_hash.hex()})
        return True

    def retry_command(self, job, code):
        row = self.session.get(BrokerInbox, job.message_id, with_for_update=True)
        if row is None or row.status != InboxStatus.PENDING or row.lease_token != job.lease or row.generation != job.generation:
            return
        if row.attempts >= MAX_ATTEMPTS:
            self.finish_command(job, InboxStatus.REVIEW,
                {"messageId": str(row.message_id), "status": InboxStatus.REVIEW, "code": code}, code)
        else:
            row.lease_token, row.last_error = None, code
            row.available_at = now() + timedelta(seconds=retry_seconds(row.attempts))

    def claim_event(self):
        row = self.session.scalar(select(OutboxMessage).where(OutboxMessage.destination.in_(list(Destination)),
            OutboxMessage.published_at.is_(None), OutboxMessage.failed_at.is_(None), OutboxMessage.available_at <= func.now())
            .order_by(OutboxMessage.available_at, OutboxMessage.created_at, OutboxMessage.id)
            .limit(1).with_for_update(skip_locked=True))
        if row is None:
            return None
        if row.attempts >= MAX_ATTEMPTS:
            row.failed_at, row.lease_token = now(), None
            row.last_error = BrokerCode.PUBLICATION_UNAVAILABLE
            return None
        row.attempts += 1
        row.lease_token, row.available_at = uuid4(), now() + timedelta(seconds=LEASE_SECONDS)
        return EventJob(row.id, row.destination, row.event_name, row.schema_version, row.aggregate_id,
                        row.payload, row.lease_token, row.attempts)

    def confirm_event(self, job):
        row = self.session.get(OutboxMessage, job.id, with_for_update=True)
        if row is None or row.published_at is not None or row.lease_token != job.lease:
            return False
        row.published_at, row.lease_token, row.last_error = now(), None, None
        return True

    def retry_event(self, job):
        row = self.session.get(OutboxMessage, job.id, with_for_update=True)
        if row is None or row.published_at is not None or row.lease_token != job.lease:
            return
        row.lease_token, row.last_error = None, BrokerCode.PUBLICATION_UNAVAILABLE
        if row.attempts >= MAX_ATTEMPTS:
            row.failed_at = now()
        else:
            row.available_at = now() + timedelta(seconds=retry_seconds(row.attempts))

    def status(self, message_id):
        row = self.session.get(BrokerInbox, message_id)
        if row is None:
            return None
        events = list(self.session.scalars(select(OutboxMessage).where(OutboxMessage.aggregate_id == message_id,
            OutboxMessage.destination.in_((Destination.RESULT, Destination.DEAD))).order_by(OutboxMessage.created_at, OutboxMessage.id)))
        return {"messageId": str(row.message_id), "status": row.status, "attempts": row.attempts,
            "deliveryGeneration": row.generation, "lastError": row.last_error, "result": row.result_json,
            "events": [{"eventId": str(event.id), "destination": event.destination, "attempts": event.attempts,
                "status": "PUBLISHED" if event.published_at else "REVIEW" if event.failed_at else "PENDING"}
                       for event in events]}

    def replay_command(self, message_id):
        row = self.session.get(BrokerInbox, message_id, with_for_update=True)
        if row is None or row.status not in (InboxStatus.REVIEW, InboxStatus.REJECTED):
            return False
        row.status, row.attempts, row.generation = InboxStatus.PENDING, 0, row.generation + 1
        row.available_at, row.lease_token, row.finished_at = now(), None, None
        row.last_error, row.result_json = None, None
        return True

    def replay_event(self, event_id):
        row = self.session.get(OutboxMessage, event_id, with_for_update=True)
        if (row is None or row.destination not in set(Destination)
                or row.failed_at is None or row.published_at is not None):
            return False
        row.failed_at, row.lease_token, row.last_error, row.attempts, row.available_at = None, None, None, 0, now()
        return True
