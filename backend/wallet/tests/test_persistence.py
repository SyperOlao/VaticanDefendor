from concurrent.futures import ThreadPoolExecutor
from uuid import uuid4
import pytest
from sqlalchemy import func, select
from sqlalchemy.exc import IntegrityError
from vatican_wallet.persistence.postgres.tables import Currency, EconomyTransaction


def test_ledger_deduplicates_concurrent_delivery(wallet_storage):
    database, _ = wallet_storage
    account_id, source_id = uuid4(), uuid4()
    with database.transaction() as session:
        session.add(Currency(code="GEMS"))

    def deliver(_):
        try:
            with database.transaction() as session:
                session.add(EconomyTransaction(account_id=account_id, currency_code="GEMS", delta=10,
                                               balance_after=10, reason="PURCHASE", source_type="PAYMENT",
                                               source_id=source_id))
            return "created"
        except IntegrityError:
            return "duplicate"

    with ThreadPoolExecutor(max_workers=2) as workers:
        assert sorted(workers.map(deliver, range(2))) == ["created", "duplicate"]
    with database.transaction() as session:
        assert session.scalar(select(func.count()).select_from(EconomyTransaction)) == 1

