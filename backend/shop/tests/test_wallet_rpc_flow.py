"""The purchase saga uses persisted wallet results, including recovery and refunds."""
import json
from types import SimpleNamespace

import pytest

from vatican_wallet.application.broker import AcceptBrokerDelivery, BrokerOperations, ProcessBrokerCommands
from vatican_wallet.application.health import HealthUseCases
from vatican_wallet.bootstrap.settings import Settings as WalletSettings
from vatican_wallet.migrate import SCHEMA_REVISION
from vatican_wallet.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as WalletUow
from vatican_wallet.presentation.grpc import create_server
from vatican_shop.application.use_cases import SettlePurchases
from vatican_shop.integrations.wallet_grpc import WalletGrpcClient
from vatican_shop.integrations.wallet_purchase import WalletBrokerPurchaseGateway
from .test_purchases import world, buy, ready, weapon_count

SECRET = 'shop-wallet-rpc-tests-' + 's' * 40


@pytest.fixture
def rpc(world, wallet_storage):
    w = world;db, url = wallet_storage;uow = WalletUow(db)
    receiver = AcceptBrokerDelivery(uow)
    processor = ProcessBrokerCommands(uow, w.wallet)
    values = {BrokerOperations: BrokerOperations(uow), HealthUseCases: HealthUseCases(uow, SCHEMA_REVISION)}
    server, port = create_server(SimpleNamespace(get=values.__getitem__), WalletSettings(url, SECRET), '127.0.0.1:0')
    server.start();reader = WalletGrpcClient(f'127.0.0.1:{port}', SECRET)
    class Publisher:
        def __init__(self):self.sent = [];self.lose_confirm = False
        def publish(self, envelope):
            assert db.engine.pool.checkedout() == w.database.engine.pool.checkedout() == 0
            self.sent.append(envelope)
            receiver.execute(json.dumps(envelope).encode(), envelope['messageId'], 'application/json', 'utf-8')
            if self.lose_confirm:
                from vatican_shop.application.errors import ApplicationError
                raise ApplicationError(503, 'Injected lost confirm')
    publisher = Publisher()
    gateway = WalletBrokerPurchaseGateway(publisher, reader, w.gateway)
    w.worker = SettlePurchases(w.uow, gateway)
    w.wallet_processor, w.wallet_publisher, w.broker_gateway = processor, publisher, gateway
    try:yield w
    finally:reader.close();server.stop(0).wait()


def charge(w):
    operation, _ = buy(w)
    assert w.worker.once() and w.api.receipt(w.account, operation)['status'] == 'Pending'
    assert w.wallet.balance(w.account)['balance'] == 500 and weapon_count(w) == 0
    assert w.wallet_processor.once()
    ready(w, operation)
    # A restarted purchase worker resumes the same immutable plan.
    w.worker = SettlePurchases(w.uow, w.broker_gateway)
    assert w.worker.once() and w.wallet.balance(w.account)['balance'] == 475
    return operation


def test_confirm_loss_and_restart_complete_purchase_after_single_charge(rpc):
    w = rpc;w.wallet_publisher.lose_confirm = True
    operation = charge(w)
    assert w.worker.once() and w.api.receipt(w.account, operation)['status'] == 'Completed'
    assert w.wallet.balance(w.account)['balance'] == 475 and weapon_count(w) == 1
    assert len(w.wallet_publisher.sent) == 1


def test_grant_rejection_waits_for_refund_receipt_and_never_issues_weapon(rpc):
    w = rpc;operation = charge(w);w.definitions.ids = ['sword.common']
    assert w.worker.once()  # grant refusal -> REFUND_PENDING
    assert w.worker.once()  # enqueue refund; still Pending
    assert w.wallet.balance(w.account)['balance'] == 475
    assert w.api.receipt(w.account, operation)['status'] == 'Pending'
    assert w.wallet_processor.once();ready(w, operation)
    assert w.worker.once() and w.api.receipt(w.account, operation)['status'] == 'Rejected'
    assert w.wallet.balance(w.account)['balance'] == 500 and weapon_count(w) == 0
    assert len(w.wallet_publisher.sent) == 2


def test_insufficient_funds_is_a_final_business_rejection_without_grant(rpc):
    w = rpc;w.catalog.price = 501;operation, _ = buy(w)
    assert w.worker.once() and w.wallet_processor.once();ready(w, operation)
    assert w.worker.once() and w.api.receipt(w.account, operation)['status'] == 'Rejected'
    assert w.wallet.balance(w.account)['balance'] == 500 and weapon_count(w) == 0
