from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
from uuid import UUID, uuid4

import pytest
from fastapi.testclient import TestClient
from sqlalchemy import func, select, update

from vatican_wallet.application.errors import ApplicationError
from vatican_wallet.application.use_cases import WalletUseCases
from vatican_wallet.bootstrap.settings import Settings
from vatican_wallet.persistence.postgres.tables import (
    ConsumedMessage, CurrencyBalance, EconomyTransaction, RunRewardReceipt,
)
from vatican_wallet.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory
from vatican_wallet.presentation.app import create_app


@pytest.fixture
def wallet(wallet_storage):
    return WalletUseCases(PostgresUnitOfWorkFactory(wallet_storage[0]))


def reward(account, run=None, amount=25):
    return {"accountId": str(account), "runId": str(run or uuid4()), "amount": amount}


def deliver(wallet, payload, message=None):
    return wallet.receive(message or uuid4(), "run.currency_reward", 1, payload)


def test_run_replay_keeps_original_receipt_after_spending_and_service_restart(wallet, wallet_storage):
    account, message = uuid4(), uuid4()
    payload = reward(account)
    first = deliver(wallet, payload, message)
    wallet.change(account, uuid4(), 100)
    restarted = WalletUseCases(PostgresUnitOfWorkFactory(wallet_storage[0]))
    assert deliver(restarted, payload, message) == first
    assert deliver(restarted, payload)["receipt"] == first["receipt"]
    assert wallet.balance(account)["balance"] == 425
    with wallet_storage[0].transaction() as session:
        assert session.scalar(select(func.count()).select_from(RunRewardReceipt)) == 1
        assert session.scalar(select(func.count()).select_from(EconomyTransaction).where(
            EconomyTransaction.source_type == "RUN_REWARD")) == 1


@pytest.mark.parametrize("change", [{"accountId": str(uuid4())}, {"amount": 50}])
def test_run_source_rejects_changed_owner_or_amount_even_with_new_message(wallet, change):
    account = uuid4()
    payload = reward(account)
    deliver(wallet, payload)
    with pytest.raises(ApplicationError) as error:
        deliver(wallet, {**payload, **change})
    assert error.value.status_code == 409
    assert wallet.balance(account)["balance"] == 525


def test_parallel_run_deliveries_with_new_message_ids_apply_once(wallet):
    account = uuid4()
    payload = reward(account)
    with ThreadPoolExecutor(max_workers=6) as pool:
        results = list(pool.map(lambda _: deliver(wallet, payload), range(12)))
    assert all(result["receipt"] == results[0]["receipt"] for result in results)
    assert wallet.balance(account)["balance"] == 525


def test_parallel_changed_payloads_for_one_message_return_conflict_without_partial_grants(wallet, wallet_storage):
    message = uuid4()
    def send(_):
        try:
            deliver(wallet, reward(uuid4()), message)
            return "APPLIED"
        except ApplicationError as error:
            assert error.status_code == 409
            return "CONFLICT"
    with ThreadPoolExecutor(max_workers=6) as pool:
        results = list(pool.map(send, range(6)))
    assert results.count("APPLIED") == 1
    with wallet_storage[0].transaction() as session:
        assert session.scalar(select(func.count()).select_from(RunRewardReceipt)) == 1
        assert session.scalar(select(func.count()).select_from(CurrencyBalance)) == 1


@pytest.mark.parametrize("event", ["level.reward", "case.reward", "case.payment"])
def test_message_id_is_global_across_consumer_types(wallet, event):
    account, message = uuid4(), uuid4()
    deliver(wallet, reward(account), message)
    payload = ({"accountId": str(account), "openingId": str(uuid4()), "caseCode": "basic",
                "currencyId": "PRESTIGE", "amount": 10} if event == "case.payment" else
               {"accountId": str(account), "openingId" if event == "case.reward" else "claimId": str(uuid4()),
                "itemIndex": 0, "rewardType": "CURRENCY", "rewardRef": "GEMS", "quantity": 10})
    with pytest.raises(ApplicationError) as error:
        wallet.receive(message, event, 1, payload)
    assert error.value.status_code == 409
    assert wallet.balance(account)["balance"] == 525


def test_run_commit_failure_rolls_back_every_financial_effect(wallet, wallet_storage, monkeypatch):
    from sqlalchemy.orm import Session
    account = uuid4()
    payload = reward(account)
    flush = Session.flush
    def fail(session, *args, **kwargs):
        if any(isinstance(row, ConsumedMessage) for row in session.new):
            raise RuntimeError("simulated commit failure")
        return flush(session, *args, **kwargs)
    with monkeypatch.context() as scoped:
        scoped.setattr(Session, "flush", fail)
        with pytest.raises(RuntimeError, match="simulated"):
            deliver(wallet, payload)
    with wallet_storage[0].transaction() as session:
        for table in (CurrencyBalance, EconomyTransaction, RunRewardReceipt, ConsumedMessage):
            assert session.scalar(select(func.count()).select_from(table)) == 0
    deliver(wallet, payload)
    assert wallet.balance(account)["balance"] == 525


def test_parallel_charges_cannot_overdraw_and_refund_replay_is_stable(wallet):
    account = uuid4()
    operations = [uuid4() for _ in range(8)]
    with ThreadPoolExecutor(max_workers=4) as pool:
        results = list(pool.map(lambda operation: wallet.change(account, operation, 100), operations))
    assert sum(result["status"] == "APPLIED" for result in results) == 5
    assert wallet.balance(account)["balance"] == 0
    operation = next(operation for operation, result in zip(operations, results) if result["status"] == "APPLIED")
    first = wallet.change(account, operation, 100, refund=True)
    assert wallet.change(account, operation, 100, refund=True) == first
    assert wallet.balance(account)["balance"] == 100


def test_overflow_is_atomic_and_failed_message_can_be_retried_after_spending(wallet, wallet_storage):
    account, message = uuid4(), uuid4()
    payload = reward(account, amount=9_223_372_036_854_775_807)
    wallet.balance(account)
    with pytest.raises(ApplicationError, match="capacity"):
        deliver(wallet, payload, message)
    assert wallet.balance(account)["balance"] == 500
    with wallet_storage[0].transaction() as session:
        assert session.get(RunRewardReceipt, UUID(payload["runId"])) is None
        assert session.get(ConsumedMessage, ("rewards.v1", message)) is None
    wallet.change(account, uuid4(), 500)
    deliver(wallet, payload, message)
    assert wallet.balance(account)["balance"] == payload["amount"]


def test_history_pagination_ties_currency_and_owner_isolation(wallet, wallet_storage):
    account, other = uuid4(), uuid4()
    wallet.balance(account)
    for _ in range(4):
        deliver(wallet, reward(account))
    wallet.receive(uuid4(), "level.reward", 1, {"accountId": str(account), "claimId": str(uuid4()),
        "itemIndex": 0, "rewardType": "CURRENCY", "rewardRef": "GEMS", "quantity": 10})
    wallet.balance(other)
    with wallet_storage[0].transaction() as session:
        session.execute(update(EconomyTransaction).values(created_at=datetime(2026, 1, 1, tzinfo=timezone.utc)))
    full = wallet.history(account)["transactions"]
    cursor, paged = None, []
    while True:
        page = wallet.history(account, before=cursor, limit=2)
        paged.extend(page["transactions"])
        cursor = page["nextCursor"]
        if cursor is None:
            break
        cursor = UUID(cursor)
    assert paged == full and len(full) == 6
    assert len({row["transactionId"] for row in full}) == 6
    gems = wallet.history(account, "GEMS")["transactions"]
    assert len(gems) == 1 and gems[0]["delta"] == 10
    foreign = UUID(wallet.history(other)["transactions"][0]["transactionId"])
    for before, currency in [(foreign, None), (uuid4(), None), (UUID(gems[0]["transactionId"]), "PRESTIGE")]:
        with pytest.raises(ApplicationError) as error:
            wallet.history(account, currency, before)
        assert error.value.status_code == 422


def test_history_read_does_not_create_a_wallet(wallet, wallet_storage):
    assert wallet.history(uuid4()) == {"transactions": [], "nextCursor": None}
    with wallet_storage[0].transaction() as session:
        assert session.scalar(select(func.count()).select_from(CurrencyBalance)) == 0


@pytest.mark.parametrize("amount", [True, "25", 1.5, 0, -1, 9_223_372_036_854_775_808])
def test_invalid_reward_amount_has_no_effect(wallet, amount):
    with pytest.raises(ApplicationError) as error:
        deliver(wallet, reward(uuid4(), amount=amount))
    assert error.value.status_code == 422


def test_http_auth_history_and_strict_economy_inputs(wallet_storage):
    account = uuid4()
    class Auth:
        def authenticate(self, token):
            return type("Principal", (), {"account_id": account})()
    settings = Settings(wallet_storage[1], "s" * 32)
    user_headers = {"Authorization": "Bearer test"}
    service_headers = {"X-Service-Secret": "s" * 32}
    with TestClient(create_app(settings, auth_gateway=Auth())) as client:
        assert client.get("/v1/wallet/transactions").status_code == 401
        assert client.get("/v1/wallet/transactions", headers=service_headers).status_code == 401
        assert client.post("/internal/charges", headers=user_headers, json={
            "accountId": str(account), "operationId": str(uuid4()), "amount": 10}).status_code == 401
        for amount in (True, "10", 1.0):
            assert client.post("/internal/charges", headers=service_headers, json={
                "accountId": str(account), "operationId": str(uuid4()), "amount": amount}).status_code == 422
        assert client.post("/internal/messages", headers=service_headers, json={
            "messageId": str(uuid4()), "eventName": "run.currency_reward", "schemaVersion": True,
            "payload": reward(account)}).status_code == 422
        assert client.get("/v1/wallet", headers=user_headers).json()["balance"] == 500
        page = client.get("/v1/wallet/transactions", headers=user_headers).json()
        assert page["transactions"][0]["delta"] == 500
        for query in ("limit=0", "limit=101", "currencyId=USD", "before=invalid"):
            assert client.get("/v1/wallet/transactions?" + query, headers=user_headers).status_code == 422
