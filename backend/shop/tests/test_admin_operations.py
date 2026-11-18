from concurrent.futures import ThreadPoolExecutor
import json
from datetime import datetime, timedelta, timezone
from types import SimpleNamespace
from uuid import uuid4

from fastapi.testclient import TestClient
import pytest
from sqlalchemy import func, select

from vatican_inventory.application.use_cases import InventoryUseCases
from vatican_inventory.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as InventoryUow
from vatican_inventory.persistence.postgres.tables import OwnedWeapon
from vatican_wallet.application.use_cases import WalletUseCases
from vatican_wallet.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as WalletUow
from vatican_shop.application.errors import ApplicationError, ParticipantConflict
from vatican_shop.application.models import Principal
from vatican_shop.application.operations import AdminPurchases
from vatican_shop.application.use_cases import SettlePurchases, ShopUseCases
from vatican_shop.bootstrap.settings import Settings
from vatican_shop.persistence.postgres.repositories.state import StateRepository
from vatican_shop.persistence.postgres.tables import IdempotencyRecord, PurchaseEvent, PurchaseJob, PurchaseOperation
from vatican_shop.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory
from vatican_shop.presentation.app import create_app


@pytest.fixture
def world(shop_storage, wallet_storage, inventory_storage):
    db, url = shop_storage
    class Auth:
        actor = uuid4()
        revoked = False
        check_connections = True
        def authenticate(self, token):
            if self.check_connections:
                assert db.engine.pool.checkedout() == 0
            if token == "invalid":
                raise ApplicationError(401, "Credential expired")
            if token == "outage":
                raise ApplicationError(503, "Auth unavailable")
            return Principal(self.actor, token == "guest", ("ADMIN",) if token in ("admin", "guest") and not self.revoked else ())
    class Definitions:
        def get(self):
            return [{"Id": key, "ExperiencePerLevel": 100, "MaximumLevel": 10} for key in ("sword.common", "sword.rare")]
    class Catalog:
        def get(self):
            return {"version": "v1", "offers": [{"id": "sword", "weaponConfigurationId": "sword.rare", "price": 25,
                "purchaseLimit": 1, "available": True, "unavailableReason": ""}]}
    wallet = WalletUseCases(WalletUow(wallet_storage[0]))
    inventory = InventoryUseCases(InventoryUow(inventory_storage[0]), Definitions())
    class Gateway:
        blocked = None
        lose_ack = None
        rejected = None
        calls = []
        def call(self, step, job):
            assert db.engine.pool.checkedout() == 0
            self.calls.append(step)
            if self.blocked == step:
                raise ParticipantConflict()
            if self.rejected == step:
                return {"status": "REJECTED"}
            if step == "grant":
                result = inventory.grant(job["accountId"], job["operationId"], job["plan"]["configurationId"])
            else:
                result = wallet.change(job["accountId"], job["operationId"], job["plan"]["price"], refund=step == "refund")
            if self.lose_ack == step:
                self.lose_ack = None
                raise ApplicationError(503, "Private transport diagnostics must not be stored")
            return result
        def charge(self, job):
            return self.call("charge", job)
        def grant(self, job):
            return self.call("grant", job)
        def refund(self, job):
            return self.call("refund", job)
    auth, gateway = Auth(), Gateway()
    uow = PostgresUnitOfWorkFactory(db)
    return SimpleNamespace(db=db, url=url, uow=uow, auth=auth, gateway=gateway, wallet=wallet, inventory_db=inventory_storage[0],
        api=ShopUseCases(uow, Catalog()), admin=AdminPurchases(uow, auth), worker=SettlePurchases(uow, gateway),
        account=uuid4(), operation=uuid4())


def review(w):
    w.gateway.blocked = "grant"
    w.api.purchase(w.account, w.operation, "sword", "v1")
    assert w.worker.once() and w.worker.once()
    return w.admin.get("admin", w.account, w.operation)


def drain(w):
    for _ in range(10):
        if not w.worker.once():
            return
    pytest.fail("Purchase did not settle")


def retry(w, row, key=None):
    return w.admin.retry("admin", w.account, w.operation, row["job"]["version"], key or uuid4(), "Participants checked")


def test_review_inspection_and_ordered_journal(world):
    w = world
    row = review(w)
    assert row["canRetry"] and row["resumeState"] == "CHARGED"
    assert row["plan"] == {"price": 25, "configurationId": "sword.rare"}
    assert row["job"]["lastErrorCode"] == "ParticipantConflict"
    assert row["job"]["attempts"] == 1
    assert "plan" not in row["receipt"] and "_resumeState" not in row["receipt"]
    assert "lease" not in row["job"]
    rows = w.admin.events("admin", w.account, w.operation)["events"]
    assert [r["kind"] for r in rows] == ["CREATED", "CLAIMED", "CHARGED", "CLAIMED", "REVIEW"]
    assert [r["sequence"] for r in rows] == list(range(1, 6))
    assert w.admin.list("admin", status="Review")["purchases"][0]["operationId"] == str(w.operation)
    assert w.admin.list("admin", status="Completed")["purchases"] == []


def test_admin_retry_completes_without_second_charge_and_replays_after_completion(world):
    w = world
    row = review(w)
    w.gateway.blocked = None
    key = uuid4()
    queued = retry(w, row, key)
    drain(w)
    assert w.api.receipt(w.account, w.operation)["status"] == "Completed"
    assert w.wallet.balance(w.account)["balance"] == 475
    with w.inventory_db.transaction() as session:
        assert session.scalar(select(func.count()).select_from(OwnedWeapon).where(
            OwnedWeapon.account_id == w.account, OwnedWeapon.configuration_id == "sword.rare")) == 1
    replay = retry(w, row, key)
    assert replay == {**queued, "replayed": True}
    events = w.admin.events("admin", w.account, w.operation)["events"]
    changes = [event for event in events if event["kind"] == "ADMIN_RETRY"]
    assert len(changes) == 1 and changes[0]["actorId"] == str(w.auth.actor)
    assert changes[0]["reason"] == "Participants checked" and changes[0]["requestKey"] == str(key)
    assert w.gateway.calls.count("charge") == 1
    with pytest.raises(ApplicationError) as error:
        w.admin.retry("admin", w.account, w.operation, row["job"]["version"], key, "Changed reason")
    assert error.value.status_code == 409


def test_lost_grant_response_recovers_with_the_original_source(world):
    w = world
    w.api.purchase(w.account, w.operation, "sword", "v1")
    w.worker.once()
    with w.db.transaction() as session:
        session.get(PurchaseJob, (w.account, w.operation)).attempts = 19
    w.gateway.lose_ack = "grant"
    w.worker.once()
    row = w.admin.get("admin", w.account, w.operation)
    assert row["status"] == "Review" and row["job"]["attempts"] == 20
    retry(w, row)
    drain(w)
    assert w.api.receipt(w.account, w.operation)["status"] == "Completed"
    assert w.wallet.balance(w.account)["balance"] == 475
    with w.inventory_db.transaction() as session:
        assert session.scalar(select(func.count()).select_from(OwnedWeapon).where(
            OwnedWeapon.account_id == w.account, OwnedWeapon.configuration_id == "sword.rare")) == 1


def test_temporary_failure_records_safe_code_and_cannot_bypass_backoff(world):
    w = world
    w.api.purchase(w.account, w.operation, "sword", "v1")
    w.worker.once()
    w.gateway.lose_ack = "grant"
    w.worker.once()
    row = w.admin.get("admin", w.account, w.operation)
    assert row["job"]["state"] == "CHARGED" and row["job"]["nextAttemptAt"] is not None
    assert row["job"]["leaseExpiresAt"] is None and not row["canRetry"]
    assert row["job"]["lastErrorCode"] == "DeliveryUnconfirmed"
    events = w.admin.events("admin", w.account, w.operation)
    assert events["events"][-1]["kind"] == "RETRY_SCHEDULED"
    assert "Private transport diagnostics" not in json.dumps(events)
    with pytest.raises(ApplicationError) as error:
        retry(w, row)
    assert error.value.status_code == 409


@pytest.mark.parametrize("step,resume,balance,status,weapons", [
    ("charge", "PENDING", 475, "Completed", 1),
    ("refund", "REFUND_PENDING", 500, "Rejected", 0),
])
def test_lost_wallet_ack_is_retried_without_a_second_financial_effect(world, step, resume, balance, status, weapons):
    w = world
    w.api.purchase(w.account, w.operation, "sword", "v1")
    if step == "refund":
        w.gateway.rejected = "grant"
        assert w.worker.once() and w.worker.once()
    with w.db.transaction() as session:
        session.get(PurchaseJob, (w.account, w.operation)).attempts = 19
    w.gateway.lose_ack = step
    assert w.worker.once()
    row = w.admin.get("admin", w.account, w.operation)
    assert row["status"] == "Review" and row["resumeState"] == resume
    retry(w, row)
    drain(w)
    assert w.api.receipt(w.account, w.operation)["status"] == status
    assert w.wallet.balance(w.account)["balance"] == balance
    with w.inventory_db.transaction() as session:
        assert session.scalar(select(func.count()).select_from(OwnedWeapon).where(
            OwnedWeapon.account_id == w.account, OwnedWeapon.configuration_id == "sword.rare")) == weapons


@pytest.mark.parametrize("method", ["list", "get", "events", "retry"])
@pytest.mark.parametrize("token,status", [("player", 403), ("guest", 403), ("invalid", 401), ("outage", 503)])
def test_use_cases_check_admin_independently_of_http(world, method, token, status):
    w = world
    calls = {"list": lambda: w.admin.list(token), "get": lambda: w.admin.get(token, w.account, w.operation),
        "events": lambda: w.admin.events(token, w.account, w.operation),
        "retry": lambda: w.admin.retry(token, w.account, w.operation, 1, uuid4(), "Checked")}
    with pytest.raises(ApplicationError) as error:
        calls[method]()
    assert error.value.status_code == status
    assert w.gateway.calls == []


def test_role_revocation_also_blocks_replay(world):
    w = world
    row = review(w)
    key = uuid4()
    retry(w, row, key)
    w.auth.revoked = True
    with pytest.raises(ApplicationError) as error:
        retry(w, row, key)
    assert error.value.status_code == 403


def test_stale_version_and_active_or_terminal_jobs_cannot_be_retried(world):
    w = world
    w.api.purchase(w.account, w.operation, "sword", "v1")
    row = w.admin.get("admin", w.account, w.operation)
    assert not row["canRetry"]
    with pytest.raises(ApplicationError) as error:
        retry(w, row)
    assert error.value.status_code == 409
    drain(w)
    with pytest.raises(ApplicationError):
        retry(w, w.admin.get("admin", w.account, w.operation))


def test_concurrent_admin_retries_have_one_winner(world):
    w = world
    row = review(w)
    w.auth.check_connections = False
    def command(_):
        try:
            return retry(w, row)["status"]
        except ApplicationError as error:
            return error.status_code
    with ThreadPoolExecutor(max_workers=2) as pool:
        results = list(pool.map(command, range(2)))
    assert sorted(map(str, results)) == ["409", "Pending"]
    assert len([r for r in w.admin.events("admin", w.account, w.operation)["events"] if r["kind"] == "ADMIN_RETRY"]) == 1


def test_concurrent_same_command_has_one_effect_and_one_replay(world):
    w = world
    row = review(w)
    w.auth.check_connections = False
    key = uuid4()
    with ThreadPoolExecutor(max_workers=2) as pool:
        results = list(pool.map(lambda _: retry(w, row, key), range(2)))
    assert sorted(r["replayed"] for r in results) == [False, True]
    assert len([r for r in w.admin.events("admin", w.account, w.operation)["events"] if r["kind"] == "ADMIN_RETRY"]) == 1


def test_two_different_admins_cannot_retry_the_same_review_version(world):
    w = world
    row = review(w)
    actors = {"admin": w.auth.actor, "second": uuid4()}
    w.auth.authenticate = lambda token: Principal(actors[token], False, ("ADMIN",))
    def command(token):
        try:
            return w.admin.retry(token, w.account, w.operation, row["job"]["version"], uuid4(), "Checked")["status"]
        except ApplicationError as error:
            return error.status_code
    with ThreadPoolExecutor(max_workers=2) as pool:
        results = list(pool.map(command, actors))
    assert sorted(map(str, results)) == ["409", "Pending"]
    assert len([r for r in w.admin.events("admin", w.account, w.operation)["events"] if r["kind"] == "ADMIN_RETRY"]) == 1


def test_stale_review_version_is_not_accepted(world):
    w = world
    row = review(w)
    with pytest.raises(ApplicationError) as error:
        w.admin.retry("admin", w.account, w.operation, row["job"]["version"] - 1, uuid4(), "Checked")
    assert error.value.status_code == 409
    assert w.admin.get("admin", w.account, w.operation)["job"]["version"] == row["job"]["version"]


def test_expired_twentieth_attempt_is_reviewed_without_a_twenty_first_send(world):
    w = world
    w.api.purchase(w.account, w.operation, "sword", "v1")
    with w.db.transaction() as session:
        job = session.get(PurchaseJob, (w.account, w.operation))
        job.attempts = 20
        job.lease_token = uuid4()
        job.available_at = datetime.now(timezone.utc) - timedelta(seconds=1)
    assert w.worker.once()
    assert w.gateway.calls == []
    row = w.admin.get("admin", w.account, w.operation)
    assert row["canRetry"] and row["resumeState"] == "PENDING"
    assert row["job"]["lastErrorCode"] == "ReconciliationRequired"
    assert row["job"]["attempts"] == 20


def test_corrupt_legacy_plan_is_not_recoverable(world):
    w = world
    review(w)
    with w.db.transaction() as session:
        op = session.get(PurchaseOperation, (w.account, w.operation))
        op.receipt_json = {**op.receipt_json, "plan": {"price": True, "configurationId": "sword.rare"}}
    row = w.admin.get("admin", w.account, w.operation)
    assert not row["canRetry"]
    with pytest.raises(ApplicationError) as error:
        retry(w, row)
    assert error.value.status_code == 409


def test_audit_failure_rolls_back_job_and_idempotency_record(world, monkeypatch):
    w = world
    row = review(w)
    original = StateRepository._event
    def fail(self, *args, **kwargs):
        original(self, *args, **kwargs)
        self.session.flush()
        raise RuntimeError("Audit failed")
    monkeypatch.setattr(StateRepository, "_event", fail)
    with pytest.raises(RuntimeError):
        retry(w, row)
    assert w.admin.get("admin", w.account, w.operation)["job"]["version"] == row["job"]["version"]
    with w.db.transaction() as session:
        assert session.scalar(select(func.count()).select_from(IdempotencyRecord)) == 0
        assert session.scalar(select(func.count()).select_from(PurchaseEvent)) == 5


def test_stale_lease_does_not_append_an_event(world):
    w = world
    w.api.purchase(w.account, w.operation, "sword", "v1")
    with w.uow() as work:
        job = work.repository.claim()
    with w.uow() as work:
        assert not work.repository.transition({**job, "lease": uuid4()}, "REJECTED", {"status": "Rejected"})
    assert len(w.admin.events("admin", w.account, w.operation)["events"]) == 2


def test_global_pagination_disambiguates_same_operation_uuid_between_accounts(world):
    w = world
    accounts = [uuid4() for _ in range(5)]
    for account in accounts:
        w.api.purchase(account, w.operation, "sword", "v1")
    at = datetime(2026, 11, 17, tzinfo=timezone.utc)
    with w.db.transaction() as session:
        for row in session.scalars(select(PurchaseOperation)):
            row.created_at = at
    first = w.admin.list("admin", limit=2)
    w.api.purchase(uuid4(), uuid4(), "sword", "v1")
    second = w.admin.list("admin", limit=2, before=first["nextCursor"])
    third = w.admin.list("admin", limit=2, before=second["nextCursor"])
    all_rows = [row for page in [first, second, third] for row in page["purchases"]]
    assert [r["accountId"] for r in all_rows] == [str(a) for a in sorted(accounts, reverse=True)]
    assert third["nextCursor"] is None
    assert len(w.admin.list("admin", account_id=accounts[0])["purchases"]) == 1
    with pytest.raises(ApplicationError) as error:
        w.admin.list("admin", before="bad-cursor")
    assert error.value.status_code == 422


def test_http_review_inspection_events_and_idempotent_retry(world):
    w = world
    row = review(w)
    app = create_app(Settings(w.url, "s" * 32), auth_gateway=w.auth)
    headers = {"Authorization": "Bearer admin"}
    path = f"/v1/admin/shop/accounts/{w.account}/purchases/{w.operation}"
    with TestClient(app) as client:
        assert client.get(path).status_code == 401
        assert client.get(path, headers={"Authorization": "Bearer player"}).status_code == 403
        assert client.get("/v1/admin/shop/purchases?status=Review", headers=headers).json()["purchases"][0]["canRetry"]
        events = client.get(path + "/events?limit=2", headers=headers).json()
        assert [r["sequence"] for r in events["events"]] == [1, 2] and events["nextCursor"] == 2
        body = {"expectedVersion": row["job"]["version"], "requestKey": str(uuid4()), "reason": "Participants checked"}
        first = client.post(path + "/retry", headers=headers, json=body)
        assert first.status_code == 200 and first.json()["status"] == "Pending"
        assert client.post(path + "/retry", headers=headers, json=body).json()["replayed"]
        assert client.post(path + "/retry", headers=headers, json={**body, "reason": "Changed"}).status_code == 409
        invalid = client.post(path + "/retry", headers=headers, json={**body, "reason": "   "})
        assert invalid.status_code == 422
        assert client.get(path + "/events?after=9223372036854775808", headers=headers).status_code == 422
        assert client.get("/v1/admin/shop/purchases?status=CHARGED", headers=headers).status_code == 422
