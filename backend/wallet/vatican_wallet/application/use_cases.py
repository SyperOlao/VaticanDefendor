import hashlib
from uuid import UUID
from .ports import UnitOfWorkFactory
from .catalog import CurrencyCode, SourceType, EventName
from .level_rewards import LevelRewardConsumer
from .cases import CaseFundingConsumer
from .prestige import PrestigePaymentConsumer
from .errors import ApplicationError
from .run_rewards import RunRewardConsumer


class WalletUseCases:
    def __init__(self, uow: UnitOfWorkFactory):
        self.uow = uow
        self.prestige_payments = PrestigePaymentConsumer(uow)
        self.case_funding = CaseFundingConsumer(uow)
        self.level_rewards = LevelRewardConsumer(uow)
        self.run_rewards = RunRewardConsumer(uow)

    def balance(self, account_id):
        with self.uow() as work:
            repo = work.repository
            repo.lock_player(account_id)
            repo.ensure(account_id)
            row = repo.balance(account_id)
            return {"accountId": str(account_id), "currencyId": CurrencyCode.PRESTIGE,
                    "balance": row.balance, "revision": row.revision,
                    "balances": [{"currencyId": r.currency_code, "balance": r.balance, "revision": r.revision}
                                 for r in repo.balances(account_id)]}

    def receive(self, message_id, event_name, schema_version, payload):
        if event_name in (EventName.PURCHASE_CHARGE, EventName.PURCHASE_REFUND):
            if (type(schema_version) is not int or schema_version != 1 or not isinstance(payload, dict)
                    or set(payload) != {'accountId', 'operationId', 'amount'}):
                raise ApplicationError(422, 'Invalid purchase command')
            try:
                account, operation = UUID(payload['accountId']), UUID(payload['operationId'])
                if not account.int or not operation.int:
                    raise ValueError()
            except (ValueError, TypeError, AttributeError):
                raise ApplicationError(422, 'Invalid purchase identifiers') from None
            receipt = self.change(account, operation, payload['amount'], refund=event_name == EventName.PURCHASE_REFUND)
            return {'messageId': str(message_id), 'status': 'APPLIED', 'receipt': receipt}
        if event_name == EventName.PRESTIGE_PAYMENT:
            return self.prestige_payments.receive(message_id,event_name,schema_version,payload)
        if event_name == EventName.CASE_FUNDING:
            return self.case_funding.receive(message_id,event_name,schema_version,payload)
        if event_name in (EventName.LEVEL_REWARD,EventName.CASE_REWARD):
            return self.level_rewards.receive(message_id, event_name, schema_version, payload)
        return self.run_rewards.receive(message_id, event_name, schema_version, payload)

    def history(self, account_id, currency=None, before=None, limit=50):
        if (not isinstance(account_id, UUID) or type(limit) is not int or not 1 <= limit <= 100
                or currency is not None and currency not in set(CurrencyCode)
                or before is not None and not isinstance(before, UUID)):
            raise ApplicationError(422, "Invalid wallet history range")
        with self.uow() as work:
            rows = work.repository.history(account_id, currency, before, limit + 1)
        return {"transactions": rows[:limit],
                "nextCursor": rows[limit - 1]["transactionId"] if len(rows) > limit else None}

    def change(self, account_id, operation_id, amount, refund=False):
        if type(amount) is not int or not 1 <= amount <= 9_223_372_036_854_775_807:
            raise ApplicationError(422, "Invalid wallet amount")
        operation = "wallet.refund" if refund else "wallet.charge"
        digest = hashlib.sha256(str(amount).encode()).digest()
        with self.uow() as work:
            repo = work.repository
            repo.lock_player(account_id)
            prior = repo.command(account_id, operation, operation_id, digest)
            if prior is not None:
                return {**prior, "accountId": str(account_id), "amount": amount,
                        "currencyId": CurrencyCode.PRESTIGE}
            repo.ensure(account_id)
            if refund and not repo.refundable(account_id, operation_id, amount):
                result = {"operationId": str(operation_id), "status": "REJECTED", "code": "ChargeNotFound"}
            else:
                try:
                    balance = repo.apply(account_id, amount if refund else -amount,
                                         SourceType.REFUND if refund else SourceType.PURCHASE, operation_id)
                except ApplicationError as error:
                    if error.detail != "Insufficient funds":
                        raise
                    result = {"operationId": str(operation_id), "status": "REJECTED", "code": "InsufficientFunds"}
                else:
                    result = {"operationId": str(operation_id), "balance": balance, "status": "APPLIED"}
            result = {**result, "accountId": str(account_id), "amount": amount,
                      "currencyId": CurrencyCode.PRESTIGE}
            repo.record_command(account_id, operation, operation_id, digest, result)
            return result
