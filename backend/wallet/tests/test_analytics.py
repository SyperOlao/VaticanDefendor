from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone
from uuid import UUID, uuid4

import httpx
import pytest
from fastapi.testclient import TestClient
from sqlalchemy import func, select, update

from vatican_wallet.application.analytics import DeliverAnalytics
from vatican_wallet.application.errors import ApplicationError
from vatican_wallet.application.use_cases import WalletUseCases
from vatican_wallet.domain.analytics import ECONOMY_EVENT, expected_receipt, message_id
from vatican_wallet.integrations.analytics import HttpAnalyticsGateway
from vatican_wallet.persistence.postgres.tables import CurrencyBalance, EconomyTransaction, OutboxMessage
from vatican_wallet.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory


@pytest.fixture
def wallet(wallet_storage):
    return WalletUseCases(PostgresUnitOfWorkFactory(wallet_storage[0]))


def due(database):
    with database.transaction() as session:
        session.execute(update(OutboxMessage).where(OutboxMessage.published_at.is_(None))
            .values(available_at=datetime.now(timezone.utc) - timedelta(seconds=1)))


def messages(database):
    with database.transaction() as session:
        return list(session.scalars(select(OutboxMessage).order_by(OutboxMessage.created_at, OutboxMessage.id)))


def test_every_committed_ledger_source_enqueues_once_including_initial_money(wallet, wallet_storage):
    database = wallet_storage[0]
    account, run, claim, opening, operation = (uuid4() for _ in range(5))
    run_body = {"accountId": str(account), "runId": str(run), "amount": 25}
    wallet.receive(uuid4(), "run.currency_reward", 1, run_body)
    for event, source in (("level.reward", claim), ("case.reward", opening)):
        wallet.receive(uuid4(), event, 1, {"accountId": str(account),
            "claimId" if event == "level.reward" else "openingId": str(source), "itemIndex": 0,
            "rewardType": "CURRENCY", "rewardRef": "GEMS", "quantity": 10})
    wallet.change(account, operation, 25)
    wallet.change(account, operation, 25, refund=True)
    wallet.receive(uuid4(), "case.payment", 1, {"accountId": str(account), "openingId": str(uuid4()),
        "caseCode": "basic", "currencyId": "GEMS", "amount": 5})
    before = messages(database)
    assert len(before) == 7
    assert {row.payload["sourceType"] for row in before} == {
        "BOOTSTRAP", "RUN_REWARD", "LEVEL_REWARD", "CASE_REWARD", "PURCHASE", "REFUND", "CASE_OPENING"}
    with database.transaction() as session:
        ledger = list(session.scalars(select(EconomyTransaction)))
        assert {row.aggregate_id for row in before} == {row.id for row in ledger}
        assert all(row.id == message_id(row.aggregate_id) and row.event_name == ECONOMY_EVENT for row in before)
        for transaction in ledger:
            payload = next(row.payload for row in before if row.aggregate_id == transaction.id)
            assert (payload["delta"], payload["balanceAfter"], payload["occurredAt"]) == (
                transaction.delta, transaction.balance_after, transaction.created_at.isoformat())
    wallet.receive(uuid4(), "run.currency_reward", 1, run_body)
    wallet.change(account, operation, 25)
    wallet.change(account, operation, 25, refund=True)
    wallet.change(account, uuid4(), 10_000)  # durable refusal has no financial effect
    assert [row.id for row in messages(database)] == [row.id for row in before]


def test_outbox_failure_rolls_back_money_and_ledger(wallet, wallet_storage, monkeypatch):
    from sqlalchemy.orm import Session
    database = wallet_storage[0]
    flush = Session.flush
    def fail(session, *args, **kwargs):
        if any(isinstance(row, OutboxMessage) for row in session.new):
            raise RuntimeError("outbox commit failed")
        return flush(session, *args, **kwargs)
    with monkeypatch.context() as scoped:
        scoped.setattr(Session, "flush", fail)
        with pytest.raises(RuntimeError, match="outbox commit"):
            wallet.balance(uuid4())
    with database.transaction() as session:
        for table in (CurrencyBalance, EconomyTransaction, OutboxMessage):
            assert session.scalar(select(func.count()).select_from(table)) == 0


def test_lost_analytics_ack_replays_same_event_without_second_credit(wallet, wallet_storage, analytics_actor):
    database = wallet_storage[0]
    account = uuid4()
    wallet.balance(account)
    class Gateway:
        fail = True
        def deliver(self, message):
            assert database.engine.pool.checkedout() == 0
            result = analytics_actor.receive(message["id"], ECONOMY_EVENT, 1, message["payload"])
            if self.fail:
                self.fail = False
                raise ApplicationError(503, "ack lost after projection")
            return result["receipt"]
    worker = DeliverAnalytics(PostgresUnitOfWorkFactory(database), Gateway())
    assert worker.once()
    first = messages(database)[0]
    assert first.published_at is None and first.attempts == 1
    due(database)
    assert worker.once() and not worker.once()
    assert messages(database)[0].published_at is not None
    assert len(analytics_actor.projection.tables["fact_economy_transactions"]) == 1
    assert wallet.balance(account)["balance"] == 500


def test_unavailable_and_wrong_receipt_remain_retryable(wallet, wallet_storage):
    database = wallet_storage[0]
    wallet.balance(uuid4())
    class Gateway:
        mode = "unavailable"
        def deliver(self, message):
            if self.mode == "unavailable":
                raise ApplicationError(503, "offline")
            receipt = expected_receipt(message["payload"])
            return {**receipt, "payloadHash": "incorrect"} if self.mode == "invalid" else receipt
    gateway = Gateway()
    worker = DeliverAnalytics(PostgresUnitOfWorkFactory(database), gateway)
    for mode in ("unavailable", "invalid"):
        gateway.mode = mode
        due(database)
        assert worker.once()
        assert messages(database)[0].published_at is None
    gateway.mode = "ok"
    due(database)
    assert worker.once() and messages(database)[0].receipt_json is not None


def test_stale_lease_and_exhausted_dead_sender_require_review_then_replay(wallet, wallet_storage):
    database = wallet_storage[0]
    wallet.balance(uuid4())
    uow = PostgresUnitOfWorkFactory(database)
    with uow() as work:
        old = work.repository.claim_analytics()
    due(database)
    with uow() as work:
        new = work.repository.claim_analytics()
    with uow() as work:
        assert not work.repository.confirm_analytics(old["id"], old["lease"], expected_receipt(old["payload"]))
        work.repository.retry_analytics(old["id"], old["lease"], True)
    assert messages(database)[0].lease_token == new["lease"]
    with database.transaction() as session:
        row = session.get(OutboxMessage, new["id"])
        row.attempts = 20
    due(database)
    with uow() as work:
        assert work.repository.claim_analytics() == {"exhausted": True}
    assert messages(database)[0].failed_at is not None
    transaction = messages(database)[0].aggregate_id
    class Gateway:
        def deliver(self, message):
            return expected_receipt(message["payload"])
    worker = DeliverAnalytics(uow, Gateway())
    assert worker.requeue(transaction)
    assert worker.once() and not worker.requeue(transaction)
    assert messages(database)[0].id == old["id"]


def test_parallel_workers_claim_each_transaction_once(wallet, wallet_storage):
    database = wallet_storage[0]
    for _ in range(10):
        wallet.balance(uuid4())
    class Gateway:
        def deliver(self, message):
            return expected_receipt(message["payload"])
    worker = DeliverAnalytics(PostgresUnitOfWorkFactory(database), Gateway())
    with ThreadPoolExecutor(max_workers=5) as pool:
        assert all(pool.map(lambda _: worker.once(), range(10)))
    assert not worker.once()
    assert all(row.published_at is not None and row.attempts == 1 for row in messages(database))


def test_real_http_202_waits_for_analytics_projection(wallet, wallet_storage, analytics_storage):
    from vatican_analytics.application.use_cases import ProjectEvents
    from vatican_analytics.bootstrap.settings import Settings
    from vatican_analytics.presentation.app import create_app
    from vatican_analytics.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as AnalyticsUow
    database = wallet_storage[0]
    wallet.balance(uuid4())
    secret = "wallet-analytics-test-secret-123456789"
    with TestClient(create_app(Settings(analytics_storage[1], secret, "clickhouse+http://unused/unused"))) as client:
        gateway = HttpAnalyticsGateway(client, "http://testserver", secret)
        worker = DeliverAnalytics(PostgresUnitOfWorkFactory(database), gateway)
        assert worker.once()
        assert messages(database)[0].published_at is None
        class Projection:
            def project(self, payload):
                from vatican_analytics.domain.projection import rows
                assert "fact_economy_transactions" in rows(payload)
        assert ProjectEvents(AnalyticsUow(analytics_storage[0]), Projection()).once()
        due(database)
        assert worker.once()
        assert messages(database)[0].published_at is not None


@pytest.mark.parametrize("status", [202, 401, 403, 408, 429, 500, 503])
def test_http_transient_responses_are_not_final_acknowledgements(status):
    with httpx.Client(transport=httpx.MockTransport(lambda request: httpx.Response(status))) as client:
        with pytest.raises(ApplicationError) as error:
            HttpAnalyticsGateway(client, "http://analytics", "s" * 32).deliver({
                "id": uuid4(), "event_name": ECONOMY_EVENT, "schema_version": 1, "payload": {}})
        assert error.value.status_code == 503
