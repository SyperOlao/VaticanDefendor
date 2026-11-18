import hashlib
import json
from uuid import UUID

from .catalog import EventName, MessageStatus, Operation
from .errors import ApplicationError
from .ports import UnitOfWorkFactory
from .validation import identifier
from ..domain.items import consume_quantity, item_id


class ConsumeItems:
    """The game service authorizes use; inventory only debits the owned quantity."""

    def __init__(self, uow: UnitOfWorkFactory):
        self.uow = uow

    @staticmethod
    def _validate(account_id, operation_id, item, quantity):
        identifier(account_id)
        identifier(operation_id)
        try:
            item_id(item)
            if item.startswith("case:"):
                raise ValueError()
            consume_quantity(0, quantity)
        except ValueError:
            raise ApplicationError(422, "Invalid item consumption") from None

    def execute(self, account_id, operation_id, item, quantity, *, message_id=None, message_digest=None):
        self._validate(account_id, operation_id, item, quantity)
        digest = hashlib.sha256(json.dumps([item, quantity], separators=(",", ":")).encode()).digest()
        with self.uow() as work:
            repo = work.repository
            if message_id is not None:
                identifier(message_id)
                repo.lock_message(message_id)
            repo.lock_player(account_id)
            consumed = message_id is not None and repo.received(message_id, message_digest)
            receipt = repo.command(account_id, Operation.ITEM_CONSUMPTION, operation_id, digest)
            if receipt is None:
                if consumed:
                    raise ApplicationError(409, "Item consumption message has no source receipt")
                repo.ensure(account_id)
                current = repo.quantity(account_id, item)
                remaining = consume_quantity(current, quantity)
                receipt = {"accountId": str(account_id), "operationId": str(operation_id),
                           "itemId": item, "quantity": quantity}
                if remaining is None:
                    receipt.update(status=MessageStatus.REJECTED, code="InsufficientItems", quantityAfter=current)
                else:
                    repo.set_quantity(account_id, item, remaining)
                    receipt.update(status=MessageStatus.APPLIED, quantityAfter=remaining,
                                   inventoryRevision=repo.state(account_id).revision)
                repo.record_command(account_id, Operation.ITEM_CONSUMPTION, operation_id, digest, receipt)
            if message_id is not None and not consumed:
                repo.record_message(message_id, message_digest)
            return receipt

    def get(self, account_id, operation_id):
        identifier(account_id)
        identifier(operation_id)
        with self.uow() as work:
            repo = work.repository
            repo.lock_player(account_id)
            receipt = repo.command(account_id, Operation.ITEM_CONSUMPTION, operation_id)
            if receipt is None:
                raise ApplicationError(404, "Item consumption not found")
            return receipt

    def receive(self, message_id, event_name, schema_version, payload):
        identifier(message_id)
        try:
            if (event_name != EventName.ITEM_CONSUMPTION or type(schema_version) is not int or schema_version != 1
                    or not isinstance(payload, dict)
                    or set(payload) != {"accountId", "operationId", "itemId", "quantity"}):
                raise ValueError()
            account, operation = UUID(payload["accountId"]), UUID(payload["operationId"])
            # Hash the full envelope so reusing messageId for a different source conflicts too.
            digest = hashlib.sha256(json.dumps([event_name, schema_version, payload], sort_keys=True,
                separators=(",", ":"), allow_nan=False).encode()).digest()
        except (ValueError, TypeError, KeyError, AttributeError):
            raise ApplicationError(422, "Invalid item consumption message") from None
        receipt = self.execute(account, operation, payload["itemId"], payload["quantity"],
                               message_id=message_id, message_digest=digest)
        return {"messageId": str(message_id), "status": MessageStatus.APPLIED, "receipt": receipt}
