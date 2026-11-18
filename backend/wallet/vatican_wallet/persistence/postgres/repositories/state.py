from uuid import uuid5
from datetime import datetime, timezone
from sqlalchemy import select, tuple_
from sqlalchemy.dialects.postgresql import insert
from .messaging import MessagingRepository
from .analytics import AnalyticsRepository
from ..tables import Currency, CurrencyBalance, EconomyTransaction, IdempotencyRecord
from ....application.catalog import CurrencyCode, SourceType
from ....application.errors import ApplicationError


class StateRepository(MessagingRepository, AnalyticsRepository):
    def history(self, account_id, currency, before, limit):
        statement = select(EconomyTransaction).where(EconomyTransaction.account_id == account_id)
        if currency is not None:
            statement = statement.where(EconomyTransaction.currency_code == currency)
        if before is not None:
            cursor = self.session.scalar(statement.where(EconomyTransaction.id == before))
            if cursor is None:
                raise ApplicationError(422, "Invalid wallet history cursor")
            statement = statement.where(tuple_(EconomyTransaction.created_at, EconomyTransaction.id)
                                        < tuple_(cursor.created_at, cursor.id))
        rows = self.session.scalars(statement.order_by(EconomyTransaction.created_at.desc(),
                                                       EconomyTransaction.id.desc()).limit(limit))
        return [{"transactionId": str(row.id), "currencyId": row.currency_code, "delta": row.delta,
                 "balanceAfter": row.balance_after, "reason": row.reason, "sourceType": row.source_type,
                 "sourceId": str(row.source_id), "itemIndex": row.line_index,
                 "createdAt": row.created_at.isoformat()} for row in rows]

    def ensure(self, account_id):
        self.session.execute(insert(Currency).values(code=CurrencyCode.PRESTIGE).on_conflict_do_nothing())
        created = self.session.scalar(insert(CurrencyBalance).values(account_id=account_id,
            currency_code=CurrencyCode.PRESTIGE, balance=500).on_conflict_do_nothing()
            .returning(CurrencyBalance.account_id))
        if created:
            self.record_transaction(EconomyTransaction(id=uuid5(account_id, "wallet.initial"), account_id=account_id,
                currency_code=CurrencyCode.PRESTIGE, delta=500, balance_after=500, reason="INITIAL_BALANCE",
                source_type=SourceType.BOOTSTRAP, source_id=account_id, line_index=0,
                created_at=datetime.now(timezone.utc)))

    def balance(self, account_id, currency=CurrencyCode.PRESTIGE):
        return self.session.get(CurrencyBalance, (account_id, currency))

    def apply(self, account_id, amount, source_type, source_id, currency=CurrencyCode.PRESTIGE, line_index=0):
        prior = self.session.scalar(select(EconomyTransaction).where(
            EconomyTransaction.account_id == account_id, EconomyTransaction.currency_code == currency,
            EconomyTransaction.source_type == source_type, EconomyTransaction.source_id == source_id,
            EconomyTransaction.line_index == line_index))
        if prior is not None:
            if prior.delta != amount:
                raise ApplicationError(409, "Operation amount changed")
            return prior.balance_after
        balance = self.balance(account_id, currency)
        if balance.balance + amount < 0:
            raise ApplicationError(409, "Insufficient funds")
        if balance.balance + amount > 9_223_372_036_854_775_807:
            raise ApplicationError(409, "Wallet capacity exceeded")
        balance.balance += amount
        balance.revision += 1
        self.record_transaction(EconomyTransaction(id=uuid5(account_id, "wallet:" + source_type + ":" + str(source_id) +
                (":" + str(currency) + ":" + str(line_index) if source_type in (SourceType.LEVEL_REWARD,SourceType.CASE_REWARD) else "")), account_id=account_id,
            currency_code=currency, delta=amount, balance_after=balance.balance,
            reason=source_type, source_type=source_type, source_id=source_id, line_index=line_index,
            created_at=datetime.now(timezone.utc)))
        return balance.balance


    def command(self, account_id, operation, operation_id, digest):
        row = self.session.get(IdempotencyRecord, (account_id, operation, str(operation_id)))
        if row is None:
            return None
        if row.payload_hash != digest:
            raise ApplicationError(409, "Wallet operation payload changed")
        return row.result_json

    def record_command(self, account_id, operation, operation_id, digest, result):
        from sqlalchemy import func
        self.session.add(IdempotencyRecord(account_id=account_id, operation=operation, request_key=str(operation_id),
            payload_hash=digest, status="COMPLETED" if result["status"] == "APPLIED" else "REJECTED",
            result_json=result, completed_at=func.now()))

    def refundable(self, account_id, operation_id, amount):
        transaction = self.session.scalar(select(EconomyTransaction).where(
            EconomyTransaction.account_id == account_id, EconomyTransaction.source_id == operation_id,
            EconomyTransaction.source_type == SourceType.PURCHASE,
            EconomyTransaction.currency_code == CurrencyCode.PRESTIGE, EconomyTransaction.line_index == 0))
        return transaction is not None and transaction.delta == -amount


    def award_level_currency(self, account_id, claim_id, item_index, currency, quantity, case=False):
        source_type = SourceType.CASE_REWARD if case else SourceType.LEVEL_REWARD
        self.ensure(account_id)
        self.session.execute(insert(Currency).values(code=currency).on_conflict_do_nothing())
        self.session.execute(insert(CurrencyBalance).values(account_id=account_id, currency_code=currency)
                             .on_conflict_do_nothing())
        balance = self.apply(account_id, quantity, source_type, claim_id, currency, item_index)
        self.session.flush()
        transaction_id = self.session.scalar(select(EconomyTransaction.id).where(
            EconomyTransaction.account_id == account_id, EconomyTransaction.currency_code == currency,
            EconomyTransaction.source_type == source_type,
            EconomyTransaction.source_id == claim_id, EconomyTransaction.line_index == item_index))
        return {'balanceAfter': balance, 'transactionId': str(transaction_id)}

    def balances(self, account_id):
        return list(self.session.scalars(select(CurrencyBalance).where(CurrencyBalance.account_id == account_id)
                                         .order_by(CurrencyBalance.currency_code)))

    def pay_case(self, account_id, opening_id, currency, amount):
        self.ensure(account_id)
        self.session.execute(insert(Currency).values(code=currency).on_conflict_do_nothing())
        self.session.execute(insert(CurrencyBalance).values(account_id=account_id,currency_code=currency).on_conflict_do_nothing())
        balance = self.balance(account_id,currency)
        if balance.balance < amount:
            return {'outcome':'REJECTED','code':'InsufficientFunds','balanceAfter':balance.balance}
        after = self.apply(account_id,-amount,SourceType.CASE_OPENING,opening_id,currency)
        self.session.flush()
        identity = self.session.scalar(select(EconomyTransaction.id).where(EconomyTransaction.account_id==account_id,
            EconomyTransaction.source_type==SourceType.CASE_OPENING,EconomyTransaction.source_id==opening_id))
        return {'outcome':'APPLIED','balanceAfter':after,'transactionId':str(identity)}

    def prestige_receipt(self,purchase,account,digest):
        from ..tables import PrestigePaymentReceipt
        row=self.session.get(PrestigePaymentReceipt,purchase)
        if row is None:return None
        if row.account_id!=account or row.payload_hash!=digest:raise ApplicationError(409,'Prestige payment payload changed')
        return row.receipt_json

    def record_prestige_receipt(self,purchase,account,digest,receipt):
        from ..tables import PrestigePaymentReceipt
        self.session.add(PrestigePaymentReceipt(purchase_id=purchase,account_id=account,payload_hash=digest,receipt_json=receipt))

    def pay_prestige(self,account,purchase,amount):
        self.ensure(account)
        before=self.balance(account)
        if before.balance<amount:return {'outcome':'REJECTED','rejectionCode':'InsufficientFunds','balanceAfter':before.balance}
        after=self.apply(account,-amount,SourceType.PRESTIGE_UPGRADE,purchase)
        self.session.flush()
        identity=self.session.scalar(select(EconomyTransaction.id).where(EconomyTransaction.account_id==account,
            EconomyTransaction.source_type==SourceType.PRESTIGE_UPGRADE,EconomyTransaction.source_id==purchase))
        return {'outcome':'APPLIED','balanceAfter':after,'transactionId':str(identity)}
