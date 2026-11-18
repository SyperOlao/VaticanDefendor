"""One permanent currency grant per run, independently of transport message IDs."""
import hashlib
import json
from uuid import UUID, uuid5

from .catalog import CurrencyCode, EventName, SourceType
from .errors import ApplicationError
from .ports import UnitOfWorkFactory


class RunRewardConsumer:
    def __init__(self, uow: UnitOfWorkFactory):
        self.uow = uow

    def receive(self, message_id, event_name, schema_version, payload):
        try:
            if (event_name != EventName.RUN_CURRENCY or type(schema_version) is not int or schema_version != 1
                    or not isinstance(payload, dict) or set(payload) != {"accountId", "runId", "amount"}):
                raise ValueError()
            account_id, run_id = UUID(payload["accountId"]), UUID(payload["runId"])
            amount = payload["amount"]
            if type(amount) is not int or not 1 <= amount <= 9_223_372_036_854_775_807:
                raise ValueError()
            # Preserve the digest format of the original consumer for retained inbox entries.
            digest = hashlib.sha256(json.dumps([event_name, schema_version, payload], sort_keys=True).encode()).digest()
        except (ValueError, TypeError, KeyError, AttributeError):
            raise ApplicationError(422, "Invalid currency reward message") from None
        with self.uow() as work:
            repo = work.repository
            repo.lock_message(message_id)
            repo.lock_player(uuid5(run_id, "wallet.run.reward"))
            repo.lock_player(account_id)
            consumed = repo.received(message_id, digest)
            receipt = repo.run_receipt(run_id, account_id, amount)
            if receipt is None:
                if consumed:
                    raise ApplicationError(409, "Run reward message has no source receipt")
                repo.ensure(account_id)
                balance = repo.apply(account_id, amount, SourceType.RUN_REWARD, run_id)
                receipt = {"accountId": str(account_id), "runId": str(run_id), "amount": amount,
                           "currencyId": CurrencyCode.PRESTIGE, "balanceAfter": balance}
                repo.record_run_receipt(run_id, account_id, amount, receipt)
            if not consumed:
                repo.record_message(message_id, digest)
            return {"messageId": str(message_id), "status": "APPLIED", "receipt": receipt}
