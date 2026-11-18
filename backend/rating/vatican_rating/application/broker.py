import hashlib
from uuid import UUID

from .broker_ports import BrokerPublisher
from .errors import ApplicationError
from .ports import UnitOfWorkFactory
from .use_cases import RatingUseCases
from ..domain.broker import BrokerCode, Command, InboxStatus, InvalidEnvelope, rejection_id


def identifier(value):
    if not isinstance(value, UUID):
        raise ApplicationError(422, "Invalid message identifier")


class AcceptBrokerDelivery:
    def __init__(self, uow: UnitOfWorkFactory):
        self.uow = uow

    def execute(self, body, property_id=None, content_type=None, content_encoding=None):
        # Even oversized poison messages are hashed/truncated before opening a database transaction.
        raw = (hashlib.sha256(body).digest(), b"", len(body))
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
    def __init__(self, uow: UnitOfWorkFactory, rating: RatingUseCases):
        self.uow, self.rating = uow, rating

    def once(self):
        with self.uow() as work:
            job = work.broker.claim_command()
        if job is None:
            return False
        envelope = job.envelope
        try:
            result = self.rating.receive(job.message_id, envelope["eventName"],
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
