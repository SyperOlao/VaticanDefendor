import hashlib

from .broker_ports import BrokerPublisher
from .errors import ApplicationError
from .ports import UnitOfWorkFactory
from .use_cases import InventoryUseCases
from .validation import identifier
from ..domain.broker import (BrokerCode, Command, InboxStatus, InvalidEnvelope, rejection_id,
                             InspectionKind, EventStatus)
from ..domain.pagination import PageCursor
from .browse import page_limit


class AcceptBrokerDelivery:
    def __init__(self, uow: UnitOfWorkFactory):
        self.uow = uow

    def execute(self, body, property_id=None, content_type=None, content_encoding=None):
        # Even oversized poison messages are hashed/truncated before opening a database transaction.
        raw = (hashlib.sha256(body).digest(), body[:4096], len(body))
        try:
            command = Command.parse(body, property_id, content_type, content_encoding)
        except InvalidEnvelope as error:
            identity = rejection_id(body, property_id, content_type, content_encoding, error.code)
            with self.uow() as work:
                work.broker.reject_delivery(identity, *raw, error.code)
            return  # ACK only after this durable quarantine and its dead-letter outbox commit.
        with self.uow() as work:
            accepted = work.broker.accept(command)
        if not accepted:
            identity = rejection_id(body, property_id, content_type, content_encoding, BrokerCode.MESSAGE_CONFLICT)
            with self.uow() as work:
                work.broker.reject_delivery(identity, *raw, BrokerCode.MESSAGE_CONFLICT, command.message_id)


class ProcessBrokerCommands:
    def __init__(self, uow: UnitOfWorkFactory, inventory: InventoryUseCases):
        self.uow, self.inventory = uow, inventory

    def once(self):
        with self.uow() as work:
            job = work.broker.claim_command()
        if job is None:
            return False
        envelope = job.envelope
        try:
            result = self.inventory.receive(job.message_id, envelope["eventName"],
                                            envelope["schemaVersion"], envelope["payload"])
            if (not isinstance(result, dict) or result.get("messageId") != str(job.message_id)
                    or result.get("status") != InboxStatus.APPLIED):
                raise RuntimeError("Invalid handler result")
        except ApplicationError as error:
            if error.status_code in (400, 401, 403, 404, 409, 422):
                code = BrokerCode.COMMAND_CONFLICT if error.status_code == 409 else BrokerCode.COMMAND_REJECTED
                with self.uow() as work:
                    work.broker.finish_command(job, InboxStatus.REJECTED,
                        {"messageId": str(job.message_id), "status": InboxStatus.REJECTED, "code": code}, code)
            else:
                with self.uow() as work:
                    work.broker.retry_command(job, BrokerCode.HANDLER_UNAVAILABLE)
        except Exception:
            # No payload, SQL, token, exception text or traceback is retained in delivery errors.
            with self.uow() as work:
                work.broker.retry_command(job, BrokerCode.HANDLER_FAILED)
        else:
            # If this commit fails after the business commit, replay uses the source receipt.
            with self.uow() as work:
                work.broker.finish_command(job, InboxStatus.APPLIED, result)
        return True


class PublishBrokerEvents:
    def __init__(self, uow: UnitOfWorkFactory, publisher: BrokerPublisher):
        self.uow, self.publisher = uow, publisher

    def once(self):
        with self.uow() as work:
            job = work.broker.claim_event()
        if job is None:
            return False
        try:
            self.publisher.publish(job)
        except Exception:
            with self.uow() as work:
                work.broker.retry_event(job)
        else:
            # Publisher confirm precedes this transaction. Lost confirmation may duplicate the same event ID.
            with self.uow() as work:
                work.broker.confirm_event(job)
        return True


class BrokerOperations:
    def __init__(self, uow: UnitOfWorkFactory):
        self.uow = uow

    def get(self, message_id):
        identifier(message_id)
        with self.uow() as work:
            result = work.broker.status(message_id)
            if result is None:
                raise ApplicationError(404, "Broker command not found")
            return result

    def replay(self, identity, *, outbox=False):
        identifier(identity)
        with self.uow() as work:
            return (work.broker.replay_event if outbox else work.broker.replay_command)(identity)

    def list(self, kind, limit=50, before=None, state=None):
        page_limit(limit)
        choices = {InspectionKind.COMMANDS: set(InboxStatus), InspectionKind.EVENTS: set(EventStatus),
                   InspectionKind.REJECTIONS: set(BrokerCode)}
        if not isinstance(kind, str) or kind not in choices or state is not None and (not isinstance(state, str) or state not in choices[kind]):
            raise ApplicationError(422, "Invalid broker inspection filter")
        scope = ["inventory.broker", kind, state]
        try:
            cursor = PageCursor.decode(before, scope) if before is not None else None
            if cursor is not None and cursor.revision is not None:
                raise ValueError()
            position = cursor.dated_position() if cursor is not None else None
        except (ValueError, TypeError, AttributeError):
            raise ApplicationError(422, "Invalid broker inspection cursor") from None
        with self.uow() as work:
            rows = work.broker.inspect_page(kind, limit + 1, position, state)
            visible = rows[:limit]
            next_cursor = None
            if len(rows) > limit:
                last = visible[-1]
                next_cursor = PageCursor(scope, [last.created_at.isoformat(), str(last.id)]).encode()
            return {kind: [entry.data for entry in visible], "nextCursor": next_cursor}

    def detail(self, kind, identity):
        identifier(identity)
        if kind not in (InspectionKind.EVENTS, InspectionKind.REJECTIONS):
            raise ApplicationError(422, "Invalid broker inspection kind")
        with self.uow() as work:
            result = work.broker.inspect_detail(kind, identity)
            if result is None:
                raise ApplicationError(404, "Broker record not found")
            return result
