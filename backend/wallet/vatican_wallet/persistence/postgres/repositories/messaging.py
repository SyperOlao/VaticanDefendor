from uuid import uuid5
from sqlalchemy import select, func
from ...postgres.tables import ConsumedMessage, EconomyTransaction, LevelRewardReceipt, RunRewardReceipt
from ....application.errors import ApplicationError


class MessagingRepository:
    def __init__(self, session):
        self.session = session

    def lock_player(self, account_id):
        # Transaction-scoped lock also serializes the first creation of a player's state.
        self.session.execute(select(func.pg_advisory_xact_lock(int.from_bytes(account_id.bytes[:8], "big", signed=True))))

    def lock_message(self, message_id):
        self.lock_player(uuid5(message_id, "wallet.message"))

    def run_receipt(self, run_id, account_id, amount):
        row = self.session.get(RunRewardReceipt, run_id)
        if row is None:
            # A stopped legacy transfer can copy ledger rows into an already migrated wallet.
            # Adopt that committed grant without replaying its financial effect.
            transaction = self.session.scalar(select(EconomyTransaction).where(
                EconomyTransaction.source_type == "RUN_REWARD", EconomyTransaction.source_id == run_id))
            if transaction is None:
                return None
            if (transaction.account_id != account_id or transaction.delta != amount
                    or transaction.currency_code != "PRESTIGE" or transaction.line_index != 0):
                raise ApplicationError(409, "Run reward source payload changed")
            receipt = {"accountId": str(account_id), "runId": str(run_id), "amount": amount,
                       "currencyId": "PRESTIGE", "balanceAfter": transaction.balance_after}
            self.record_run_receipt(run_id, account_id, amount, receipt)
            return receipt
        if row.account_id != account_id or row.amount != amount:
            raise ApplicationError(409, "Run reward source payload changed")
        return row.receipt_json

    def record_run_receipt(self, run_id, account_id, amount, receipt):
        self.session.add(RunRewardReceipt(run_id=run_id, account_id=account_id,
                                         amount=amount, receipt_json=receipt))

    def received(self, message_id, payload_hash):
        row = self.session.get(ConsumedMessage, ("rewards.v1", message_id))
        if row is None:
            return False
        if row.payload_hash is not None and row.payload_hash != payload_hash:
            raise ApplicationError(409, "Message payload changed")
        return True

    def record_message(self, message_id, payload_hash):
        self.session.add(ConsumedMessage(consumer="rewards.v1", message_id=message_id, payload_hash=payload_hash))


    def level_receipt(self, claim_id, item_index, account_id, digest):
        row = self.session.get(LevelRewardReceipt, (claim_id, item_index))
        if row is None:
            return None
        if row.account_id != account_id or row.payload_hash != digest:
            raise ApplicationError(409, "Level reward source payload changed")
        return row.receipt_json

    def record_level_receipt(self, claim_id, item_index, account_id, digest, receipt):
        self.session.add(LevelRewardReceipt(claim_id=claim_id, item_index=item_index, account_id=account_id,
                                           payload_hash=digest, receipt_json=receipt))

    def case_receipt(self, opening_id, item_index, account_id, digest):
        from ..tables import CaseOperationReceipt
        row = self.session.get(CaseOperationReceipt,(opening_id,item_index))
        if row is None:
            return None
        if row.account_id != account_id or row.payload_hash != digest:
            raise ApplicationError(409,'Case operation source payload changed')
        return row.receipt_json

    def record_case_receipt(self, opening_id, item_index, account_id, digest, receipt):
        from ..tables import CaseOperationReceipt
        self.session.add(CaseOperationReceipt(opening_id=opening_id,item_index=item_index,account_id=account_id,
                                              payload_hash=digest,receipt_json=receipt))
