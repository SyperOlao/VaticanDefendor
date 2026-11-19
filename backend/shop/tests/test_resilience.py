"""Fault injection against real, isolated PostgreSQL schemas and participant ledgers."""
from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
from threading import Event, local
import time
from types import SimpleNamespace
from uuid import uuid4

from dishka import Provider, Scope, provide
from fastapi.testclient import TestClient
import httpx
import psycopg
import pytest
from sqlalchemy import func, select
from sqlalchemy.exc import DBAPIError, OperationalError

from vatican_shop.application.errors import ApplicationError
from vatican_shop.application.ports import DefinitionsGateway, UnitOfWorkFactory
from vatican_shop.application.use_cases import SettlePurchases
from vatican_shop.bootstrap.settings import Settings
from vatican_shop.integrations.shop import PurchaseGateway
from vatican_shop.persistence.postgres.repositories.state import StateRepository
from vatican_shop.persistence.postgres.tables import OutboxMessage, PurchaseEvent, PurchaseJob, PurchaseOperation
from vatican_shop.presentation.app import create_app
from vatican_shop.worker import run_iteration
from vatican_wallet.persistence.postgres.tables import EconomyTransaction

from .test_purchases import buy, drain, ready, weapon_count, world


@pytest.fixture
def w(world, shop_storage, wallet_storage, inventory_storage):
    world.urls = [storage[1] for storage in (shop_storage, wallet_storage, inventory_storage)]
    world.wallet_db = wallet_storage[0]
    # Pool.checkedout() counts other workers too; track the calling thread instead.
    active = local()
    for label, database in (("shop", world.database), ("inventory", world.inventory_db)):
        original = database.transaction
        @contextmanager
        def tracked(_original=original, _label=label):
            setattr(active, _label, getattr(active, _label, 0) + 1)
            try:
                with _original() as session:
                    yield session
            finally:
                setattr(active, _label, getattr(active, _label) - 1)
        database.transaction = tracked
    world.definitions.transaction_active = lambda: getattr(active, "inventory", 0) != 0
    world.gateway.check_connections = False
    for method in ("charge", "grant", "refund"):
        original = getattr(world.gateway, method)
        def outside_transaction(job, _original=original):
            assert getattr(active, "shop", 0) == 0
            return _original(job)
        setattr(world.gateway, method, outside_transaction)
    return world


def financial_effects(w, operation):
    with w.wallet_db.transaction() as session:
        return dict(session.execute(select(EconomyTransaction.source_type, func.count()).where(
            EconomyTransaction.account_id == w.account, EconomyTransaction.source_id == operation,
            EconomyTransaction.source_type.in_(("PURCHASE", "REFUND"))).group_by(EconomyTransaction.source_type)).all())


def assert_outcome(w, operation, refund=False):
    assert w.api.receipt(w.account, operation)["status"] == ("Rejected" if refund else "Completed")
    assert w.wallet.balance(w.account)["balance"] == (500 if refund else 475)
    assert weapon_count(w) == (0 if refund else 1)
    assert financial_effects(w, operation) == ({"PURCHASE": 1, "REFUND": 1} if refund else {"PURCHASE": 1})
    with w.database.transaction() as session:
        assert session.scalar(select(func.count()).select_from(OutboxMessage)) == (0 if refund else 1)
        sequences = list(session.scalars(select(PurchaseEvent.sequence).where(
            PurchaseEvent.account_id == w.account, PurchaseEvent.operation_id == operation).order_by(PurchaseEvent.sequence)))
        assert sequences == list(range(1, len(sequences) + 1))


def advance(w, operation, step):
    if step in ("grant", "refund"):
        assert w.worker.once()
    if step == "refund":
        w.definitions.ids = ["sword.common"]
        assert w.worker.once()


def app_for(w, uow=None):
    class Overrides(Provider):
        @provide(scope=Scope.APP, provides=DefinitionsGateway)
        def catalog(self):
            return w.catalog

        @provide(scope=Scope.APP, provides=UnitOfWorkFactory)
        def work(self):
            return uow or w.uow

    class Auth:
        def authenticate(self, token):
            from vatican_shop.application.models import Principal
            return Principal(w.account, False, ())

    return create_app(Settings(w.urls[0], "s" * 32), providers=(Overrides(),), auth_gateway=Auth())


def terminate_transaction(w, session):
    # Only this test's connection is terminated, never another worker or schema.
    pid = session.connection().exec_driver_sql("SELECT pg_backend_pid()").scalar_one()
    with psycopg.connect(w.urls[0]) as control:
        assert control.execute("SELECT pg_terminate_backend(%s)", (pid,)).fetchone()[0]


def test_http_duplicate_spam_has_one_purchase_job_charge_grant_and_event(w):
    operation = uuid4()
    body = {"operationId": str(operation), "offerId": "sword", "catalogVersion": "v1"}
    with TestClient(app_for(w)) as client:
        def post(_):
            return client.post("/v1/purchases", headers={"Authorization": "Bearer player"}, json=body)
        with ThreadPoolExecutor(max_workers=16) as pool:
            responses = list(pool.map(post, range(64)))
        assert all(r.status_code in (202, 503) for r in responses)
        for response in responses:
            if response.status_code == 503:
                assert post(None).status_code == 202
        with w.database.transaction() as session:
            assert session.scalar(select(func.count()).select_from(PurchaseOperation)) == 1
            assert session.scalar(select(func.count()).select_from(PurchaseJob)) == 1
            assert session.scalar(select(func.count()).select_from(PurchaseEvent).where(PurchaseEvent.kind == "CREATED")) == 1
        with ThreadPoolExecutor(max_workers=8) as pool:
            list(pool.map(lambda _: drain(w), range(8)))
        drain(w)
        assert all(post(None).json()["replayed"] for _ in range(4))
    assert_outcome(w, operation)


def test_unique_operation_spam_cannot_bypass_offer_limit(w):
    w.catalog.limit = 1
    operations = [uuid4() for _ in range(32)]
    with ThreadPoolExecutor(max_workers=8) as pool:
        replies = list(pool.map(lambda op: buy(w, op)[1], operations))
    assert sum(reply["status"] == "Pending" for reply in replies) == 1
    assert sum(reply.get("code") == "PurchaseLimitReached" for reply in replies) == 31
    drain(w)
    winner = operations[next(i for i, reply in enumerate(replies) if reply["status"] == "Pending")]
    assert_outcome(w, winner)
    assert buy(w)[1]["code"] == "PurchaseLimitReached"


def test_parallel_distinct_purchases_cannot_overdraw_the_wallet(w):
    operations = [buy(w)[0] for _ in range(24)]
    def settle(_):
        for _ in range(100):
            if not w.worker.once():
                return
        pytest.fail("Workers did not quiesce")
    with ThreadPoolExecutor(max_workers=6) as pool:
        list(pool.map(settle, range(6)))
    settle(None)
    statuses = [w.api.receipt(w.account, op)["status"] for op in operations]
    assert statuses.count("Completed") == 20 and statuses.count("Rejected") == 4
    assert w.wallet.balance(w.account)["balance"] == 0 and weapon_count(w) == 20
    with w.wallet_db.transaction() as session:
        assert session.scalar(select(func.count()).select_from(EconomyTransaction).where(
            EconomyTransaction.account_id == w.account, EconomyTransaction.source_type == "PURCHASE")) == 20


def test_conflicting_payload_spam_never_replaces_the_winning_operation(w):
    operation = uuid4()
    def command(index):
        try:
            return w.api.purchase(w.account, operation, "sword", "v1" if index % 2 else "old")
        except ApplicationError as error:
            assert error.status_code == 409
            return None
    with ThreadPoolExecutor(max_workers=8) as pool:
        results = list(pool.map(command, range(32)))
    assert len([r for r in results if r is not None and not r["replayed"]]) == 1
    with w.database.transaction() as session:
        stored = session.get(PurchaseOperation, (w.account, operation))
        version = stored.catalog_version
        assert session.scalar(select(func.count()).select_from(PurchaseOperation)) == 1
    drain(w)
    if version == "v1":
        assert_outcome(w, operation)
    else:
        assert w.api.receipt(w.account, operation)["code"] == "CatalogChanged"
        assert w.wallet.balance(w.account)["balance"] == 500 and weapon_count(w) == 0


def test_rejected_purchase_stays_rejected_after_wallet_top_up(w):
    w.catalog.price = 600
    operation, _ = buy(w)
    drain(w)
    assert w.api.receipt(w.account, operation)["code"] == "InsufficientFunds"
    w.wallet.receive(uuid4(), "run.currency_reward", 1,
                     {"accountId": str(w.account), "runId": str(uuid4()), "amount": 200})
    assert buy(w, operation)[1]["replayed"]
    assert not w.worker.once()
    assert w.api.receipt(w.account, operation)["code"] == "InsufficientFunds"
    assert financial_effects(w, operation) == {} and weapon_count(w) == 0
    replacement, _ = buy(w)
    drain(w)
    assert w.api.receipt(w.account, replacement)["status"] == "Completed"
    assert w.wallet.balance(w.account)["balance"] == 100 and weapon_count(w) == 1


def test_free_purchase_rejection_never_sends_charge_or_refund(w):
    w.catalog.price = 0
    operation, _ = buy(w)
    w.definitions.ids = ["sword.common"]
    def forbidden(job):
        pytest.fail("A free purchase must not send a financial command")
    w.gateway.charge = w.gateway.refund = forbidden
    drain(w)
    assert w.api.receipt(w.account, operation)["code"] == "GrantRejected"
    assert w.wallet.balance(w.account)["balance"] == 500 and weapon_count(w) == 0
    assert financial_effects(w, operation) == {}
    assert buy(w, operation)[1]["replayed"]


def test_disconnect_before_purchase_commit_rolls_back_then_same_id_can_succeed(w, monkeypatch):
    operation = uuid4()
    original = StateRepository.create
    def interrupted(repo, *args, **kwargs):
        original(repo, *args, **kwargs)
        repo.session.flush()
        terminate_transaction(w, repo.session)
    with TestClient(app_for(w)) as client:
        body = {"operationId": str(operation), "offerId": "sword", "catalogVersion": "v1"}
        with monkeypatch.context() as patch:
            patch.setattr(StateRepository, "create", interrupted)
            response = client.post("/v1/purchases", headers={"Authorization": "Bearer player"}, json=body)
        assert response.status_code == 503 and response.headers["Retry-After"] == "1"
        with w.database.transaction() as session:
            assert session.get(PurchaseOperation, (w.account, operation)) is None
            assert session.scalar(select(func.count()).select_from(PurchaseEvent)) == 0
        assert client.post("/v1/purchases", headers={"Authorization": "Bearer player"}, json=body).status_code == 202
    drain(w)
    assert_outcome(w, operation)


def test_lost_purchase_commit_ack_replays_without_catalog_or_duplicate_job(w):
    operation = uuid4()
    failed = False
    @contextmanager
    def uncertain_commit():
        nonlocal failed
        with w.uow() as work:
            yield work
            created = work.session.get(PurchaseOperation, (w.account, operation)) is not None
        if created and not failed:
            failed = True
            raise OperationalError("COMMIT", {}, Exception("Private commit acknowledgement lost"), connection_invalidated=True)
    with TestClient(app_for(w, uncertain_commit)) as client:
        body = {"operationId": str(operation), "offerId": "sword", "catalogVersion": "v1"}
        first = client.post("/v1/purchases", headers={"Authorization": "Bearer player"}, json=body)
        assert first.status_code == 503
        assert "Private" not in first.text
        w.catalog.unavailable = True
        replay = client.post("/v1/purchases", headers={"Authorization": "Bearer player"}, json=body)
        assert replay.status_code == 202 and replay.json()["replayed"]
    drain(w)
    assert_outcome(w, operation)


def test_exhausted_connection_pool_returns_retryable_error_without_creating_a_purchase(w):
    operation = uuid4()
    with TestClient(app_for(w)) as client:
        held = [w.database.engine.connect() for _ in range(w.database.engine.pool.size())]
        try:
            response = client.post("/v1/purchases", headers={"Authorization": "Bearer player"}, json={
                "operationId": str(operation), "offerId": "sword", "catalogVersion": "v1"})
            assert response.status_code == 503 and response.json()["code"] == "DatabaseUnavailable"
        finally:
            for connection in held:
                connection.close()
    assert buy(w, operation)[1]["status"] == "Pending"
    drain(w)
    assert_outcome(w, operation)


@pytest.mark.parametrize("step", ["charge", "grant", "refund"])
def test_disconnect_when_recording_participant_ack_replays_original_source(w, monkeypatch, step):
    operation, _ = buy(w)
    advance(w, operation, step)
    original = StateRepository.transition
    def interrupted(repo, *args, **kwargs):
        result = original(repo, *args, **kwargs)
        repo.session.flush()
        terminate_transaction(w, repo.session)
        return result
    with monkeypatch.context() as patch:
        patch.setattr(StateRepository, "transition", interrupted)
        pool = w.database.engine.pool
        queues = [("purchase", w.worker)]
        assert run_iteration(queues, w.database) == (False, True)
        assert w.database.engine.pool is not pool
    ready(w, operation)
    while True:
        worked, failed = run_iteration(queues, w.database)
        assert not failed
        if not worked:
            break
    assert_outcome(w, operation, refund=step == "refund")


CRASH_WORKER = r'''
import json, os
from vatican_shop.persistence.postgres.database import Database
from vatican_shop.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory
from vatican_shop.application.use_cases import SettlePurchases
from vatican_wallet.persistence.postgres.database import Database as WalletDatabase
from vatican_wallet.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as WalletUow
from vatican_wallet.application.use_cases import WalletUseCases
from vatican_inventory.persistence.postgres.database import Database as InventoryDatabase
from vatican_inventory.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as InventoryUow
from vatican_inventory.application.use_cases import InventoryUseCases
urls = json.loads(os.environ['SHOP_CRASH_TEST_URLS'])
uow = PostgresUnitOfWorkFactory(Database(urls[0]))
wallet = WalletUseCases(WalletUow(WalletDatabase(urls[1])))
class Definitions:
    def get(self):
        return [{'Id': key, 'ExperiencePerLevel': 100, 'MaximumLevel': 10} for key in ('sword.common','sword.rare')]
inventory = InventoryUseCases(InventoryUow(InventoryDatabase(urls[2])), Definitions())
class Gateway:
    def charge(self, job):
        wallet.change(job['accountId'],job['operationId'],job['plan']['price'])
        os._exit(91)
    def grant(self, job):
        inventory.grant(job['accountId'],job['operationId'],job['plan']['configurationId'])
        os._exit(91)
    def refund(self, job):
        wallet.change(job['accountId'],job['operationId'],job['plan']['price'],refund=True)
        os._exit(91)
if os.environ['SHOP_CRASH_TEST_STEP'] == 'claim':
    with uow() as work:
        assert work.repository.claim() is not None
    os._exit(91)
SettlePurchases(uow,Gateway()).once()
raise RuntimeError('Crash point was not reached')
'''


@pytest.mark.parametrize("step", ["claim", "charge", "grant", "refund"])
def test_hard_worker_process_exit_recovers_after_lease_without_duplicate_effect(w, step):
    operation, _ = buy(w)
    advance(w, operation, step)
    backend = Path(__file__).resolve().parents[2]
    env = {**os.environ, "SHOP_CRASH_TEST_URLS": json.dumps(w.urls), "SHOP_CRASH_TEST_STEP": step,
           "PYTHONPATH": os.pathsep.join(str(backend / name) for name in ("shop", "wallet", "inventory"))}
    child = subprocess.run([sys.executable, "-c", CRASH_WORKER], env=env, capture_output=True, text=True, timeout=60)
    assert child.returncode == 91, child.stderr
    with w.database.transaction() as session:
        assert session.get(PurchaseJob, (w.account, operation)).lease_token is not None
    ready(w, operation)
    # A newly constructed worker represents a process restart; no local state is reused.
    w.worker = SettlePurchases(w.uow, w.gateway)
    drain(w)
    assert_outcome(w, operation, refund=step == "refund")


@pytest.mark.parametrize("step", ["charge", "grant", "refund"])
def test_slow_old_worker_cannot_overwrite_replacement_after_lease_expiry(w, step):
    operation, _ = buy(w)
    advance(w, operation, step)
    applied, release = Event(), Event()
    commands = {name: getattr(w.gateway, name) for name in ("charge", "grant", "refund")}
    def delayed(job):
        result = commands[step](job)
        applied.set()
        assert release.wait(timeout=30)
        return result
    stale_worker = SettlePurchases(w.uow, SimpleNamespace(**{**commands, step: delayed}))
    with ThreadPoolExecutor(max_workers=1) as pool:
        old = pool.submit(stale_worker.once)
        try:
            assert applied.wait(timeout=30)
            ready(w, operation)
            assert w.worker.once()
            with w.database.transaction() as session:
                version = session.get(PurchaseJob, (w.account, operation)).version
        finally:
            release.set()
        assert old.result(timeout=30)
    with w.database.transaction() as session:
        assert session.get(PurchaseJob, (w.account, operation)).version == version
    drain(w)
    assert_outcome(w, operation, refund=step == "refund")


@pytest.mark.parametrize("step", ["charge", "grant", "refund"])
@pytest.mark.parametrize("failure", ["disconnect", "timeout", "overloaded", "unavailable", "malformed"])
def test_dependency_outage_or_bad_ack_keeps_original_step_then_recovers(w, step, failure):
    operation, _ = buy(w)
    advance(w, operation, step)
    def unavailable(request):
        if failure == "disconnect":
            raise httpx.ConnectError("Private endpoint", request=request)
        if failure == "timeout":
            raise httpx.ReadTimeout("Private timeout", request=request)
        if failure == "malformed":
            return httpx.Response(200, content=b"not-json")
        return httpx.Response(429 if failure == "overloaded" else 503)
    with httpx.Client(transport=httpx.MockTransport(unavailable)) as client:
        worker = SettlePurchases(w.uow, PurchaseGateway(client, Settings(w.urls[0], "s" * 32)))
        assert worker.once()
        assert not worker.once()  # Backoff is enforced; immediate spam cannot send again.
    assert w.api.receipt(w.account, operation)["status"] == "Pending"
    with w.database.transaction() as session:
        job = session.get(PurchaseJob, (w.account, operation))
        assert job.last_error_code == "DeliveryUnconfirmed" and job.attempts == 1
    ready(w, operation)
    drain(w)
    assert_outcome(w, operation, refund=step == "refund")


PARTICIPANT_SERVER = r'''
import os
import uvicorn
if os.environ['SHOP_PARTICIPANT_KIND']=='wallet':
    from vatican_wallet.bootstrap.settings import Settings
    from vatican_wallet.presentation.app import create_app
    app=create_app(Settings(os.environ['SHOP_PARTICIPANT_DATABASE'],'s'*32))
else:
    from dishka import Provider, Scope, provide
    from vatican_inventory.application.ports import DefinitionsGateway
    from vatican_inventory.bootstrap.settings import Settings
    from vatican_inventory.presentation.app import create_app
    class Definitions:
        def get(self):
            return [{'Id':key,'ExperiencePerLevel':100,'MaximumLevel':10} for key in ('sword.common','sword.rare')]
    class Overrides(Provider):
        @provide(scope=Scope.APP,provides=DefinitionsGateway)
        def definitions(self):return Definitions()
    app=create_app(Settings(os.environ['SHOP_PARTICIPANT_DATABASE'],'s'*32),providers=(Overrides(),))
uvicorn.run(app,host='127.0.0.1',port=int(os.environ['SHOP_PARTICIPANT_PORT']),log_level='error')
'''


@pytest.fixture
def participant_process(w):
    class Server:
        process = None
        def __init__(self, kind):
            self.log = tempfile.TemporaryFile(mode="w+")
            with socket.socket() as reservation:
                reservation.bind(("127.0.0.1", 0))
                self.port = reservation.getsockname()[1]
            backend = Path(__file__).resolve().parents[2]
            self.url = f"http://127.0.0.1:{self.port}"
            self.env = {**os.environ, "SHOP_PARTICIPANT_KIND": kind, "SHOP_PARTICIPANT_PORT": str(self.port),
                "SHOP_PARTICIPANT_DATABASE": w.urls[1 if kind == "wallet" else 2],
                "PYTHONPATH": os.pathsep.join(str(backend / name) for name in ("wallet", "inventory"))}
        def start(self):
            self.process = subprocess.Popen([sys.executable, "-c", PARTICIPANT_SERVER], env=self.env,
                                            stdout=self.log, stderr=subprocess.STDOUT)
            deadline = time.monotonic() + 60
            with httpx.Client(trust_env=False, timeout=1) as client:
                while time.monotonic() < deadline:
                    if self.process.poll() is not None:
                        self.log.seek(0)
                        pytest.fail("Participant failed to start: " + self.log.read())
                    try:
                        if client.get(self.url + "/health/ready").status_code == 200:
                            return
                    except httpx.HTTPError:
                        pass
                    time.sleep(0.05)
            pytest.fail("Participant did not become ready")
        def stop(self):
            if self.process is not None and self.process.poll() is None:
                self.process.terminate()
                try:
                    self.process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    self.process.kill()
                    self.process.wait(timeout=10)
    servers = []
    def create(kind):
        server = Server(kind)
        servers.append(server)
        server.start()
        return server
    yield create
    for server in servers:
        server.stop()
        server.log.close()


@pytest.mark.parametrize("step", ["charge", "grant", "refund"])
def test_real_http_participant_stop_and_restart_resumes_the_same_purchase(w, participant_process, step):
    operation, _ = buy(w)
    advance(w, operation, step)
    server = participant_process("inventory" if step == "grant" else "wallet")
    settings = Settings(w.urls[0], "s" * 32, wallet_url=server.url, inventory_url=server.url)
    with httpx.Client(trust_env=False, timeout=2) as client:
        adapter = PurchaseGateway(client, settings)
        commands = {name: getattr(w.gateway, name) for name in ("charge", "grant", "refund")}
        w.worker = SettlePurchases(w.uow, SimpleNamespace(**{**commands, step: getattr(adapter, step)}))
        server.stop()
        for attempt in range(1, 4):
            ready(w, operation)
            assert w.worker.once()
            assert w.api.receipt(w.account, operation)["status"] == "Pending"
            with w.database.transaction() as session:
                assert session.get(PurchaseJob, (w.account, operation)).attempts == attempt
        server.start()
        ready(w, operation)
        drain(w)
        assert_outcome(w, operation, refund=step == "refund")
        server.stop()
        w.catalog.unavailable = True
        assert buy(w, operation)[1]["replayed"]
