"""Durable wallet delivery, real RPC reads, and replay after partial failure."""
from datetime import datetime, timedelta, timezone
import hashlib
import json
import os
from types import SimpleNamespace
from uuid import UUID, uuid4

import grpc
import pytest
from sqlalchemy import func, select, text

from vatican_wallet.application.broker import AcceptBrokerDelivery, BrokerOperations, ProcessBrokerCommands, PublishBrokerEvents
from vatican_wallet.application.health import HealthUseCases
from vatican_wallet.application.use_cases import WalletUseCases
from vatican_wallet.bootstrap.settings import Settings
from vatican_wallet.bootstrap.rabbit import RabbitSettings
from vatican_wallet.domain.broker import Command, InboxStatus
from vatican_wallet.integrations.rabbit import RabbitBroker, BrokerUnavailable
from vatican_wallet.migrate import SCHEMA_REVISION
from vatican_wallet.persistence.postgres.tables import BrokerInbox, BrokerRejection, EconomyTransaction, OutboxMessage
from vatican_wallet.persistence.postgres.repositories.broker import BrokerRepository
from vatican_wallet.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory
from vatican_wallet.presentation.grpc import create_server
from vatican_backend.integrations.wallet_grpc import WalletGrpcClient
from vatican_backend.integrations.wallet_rabbit import WalletRabbitDelivery
from vatican_shop.integrations.wallet_purchase import WalletBrokerPurchaseGateway
from vatican_shop.integrations.wallet_grpc import WalletGrpcClient as ShopWalletGrpcClient
from vatican_shop.domain.wallet_commands import command as purchase_command
from vatican_shop.application.errors import ApplicationError as ShopError, ParticipantConflict
from vatican_backend.application.errors import ApplicationError as GameError

SECRET = 'wallet-delivery-tests-' + 's' * 40


@pytest.fixture
def world(wallet_storage):
    db, url = wallet_storage
    uow = PostgresUnitOfWorkFactory(db)
    api = WalletUseCases(uow)
    operations = BrokerOperations(uow)
    health = HealthUseCases(uow, SCHEMA_REVISION)
    values = {BrokerOperations: operations, HealthUseCases: health}
    server, port = create_server(SimpleNamespace(get=values.__getitem__), Settings(url, SECRET), '127.0.0.1:0')
    server.start()
    reader = WalletGrpcClient(f'127.0.0.1:{port}', SECRET)
    shop_reader = ShopWalletGrpcClient(f'127.0.0.1:{port}', SECRET)
    accept = AcceptBrokerDelivery(uow)
    class Publisher:
        def __init__(self):self.sent = []
        def publish(self, command):
            assert db.engine.pool.checkedout() == 0
            self.sent.append(command)
            accept.execute(json.dumps(command).encode(), command['messageId'], 'application/json', 'utf-8')
    world = SimpleNamespace(db=db, url=url, uow=uow, api=api, accept=accept, process=ProcessBrokerCommands(uow, api),
        operations=operations, reader=reader, shop_reader=shop_reader, server=server, publisher=Publisher())
    try:
        yield world
    finally:
        reader.close()
        shop_reader.close()
        server.stop(0).wait()


def expire(w):
    with w.db.transaction() as session:
        for row in session.scalars(select(BrokerInbox).where(BrokerInbox.status == 'PENDING')):
            row.available_at = datetime.now(timezone.utc) - timedelta(seconds=1)


def job():
    return {'accountId': uuid4(), 'operationId': uuid4(), 'plan': {'price': 90}}


def accept(w, command):
    w.accept.execute(json.dumps(command).encode(), command['messageId'], 'application/json', 'utf-8')


def purchase_gateway(w):
    return WalletBrokerPurchaseGateway(w.publisher, w.shop_reader, SimpleNamespace(grant=lambda _: {'status': 'APPLIED'}))


def test_publisher_confirmation_is_pending_then_duplicate_commands_charge_and_refund_once(world):
    w = world; purchase = job(); base = w.api.balance(purchase['accountId'])['balance']; gateway = purchase_gateway(w)
    with pytest.raises(ShopError) as error:gateway.charge(purchase)
    assert error.value.status_code == 503 and w.api.balance(purchase['accountId'])['balance'] == base
    assert w.process.once()
    receipt = gateway.charge(purchase)
    assert receipt['status'] == 'APPLIED' and receipt['balance'] == base - 90
    for _ in range(8):accept(w, purchase_command(purchase))
    assert not w.process.once() and gateway.charge(purchase) == receipt
    with pytest.raises(ShopError):gateway.refund(purchase)
    assert w.process.once()
    refund = gateway.refund(purchase)
    assert refund['status'] == 'APPLIED' and refund['balance'] == base
    assert gateway.refund(purchase) == refund
    assert w.api.balance(purchase['accountId'])['balance'] == base
    with w.db.transaction() as session:
        assert session.scalar(select(func.count()).select_from(BrokerInbox)) == 2


def test_saved_business_rejection_never_turns_into_a_charge_after_later_funding(world):
    w = world; purchase = job(); purchase['plan']['price'] = 10000; gateway = purchase_gateway(w)
    with pytest.raises(ShopError):gateway.charge(purchase)
    assert w.process.once()
    rejected = gateway.charge(purchase)
    assert rejected['status'] == 'REJECTED' and rejected['code'] == 'InsufficientFunds'
    w.api.receive(uuid4(), 'run.currency_reward', 1, {'accountId': str(purchase['accountId']), 'runId': str(uuid4()), 'amount': 10000})
    assert gateway.charge(purchase) == rejected


def test_commit_before_lost_result_commit_replays_financial_receipt(world, monkeypatch):
    w = world; purchase = job(); base = w.api.balance(purchase['accountId'])['balance']; accept(w, purchase_command(purchase))
    original = BrokerRepository.finish_command
    def lost(*args, **kwargs):raise OSError('injected result commit failure')
    monkeypatch.setattr(BrokerRepository, 'finish_command', lost)
    with pytest.raises(OSError):w.process.once()
    assert w.api.balance(purchase['accountId'])['balance'] == base - 90
    assert w.reader.command_status(UUID(purchase_command(purchase)['messageId']))['status'] == 'PENDING'
    monkeypatch.setattr(BrokerRepository, 'finish_command', original);expire(w)
    assert ProcessBrokerCommands(w.uow, WalletUseCases(w.uow)).once()
    assert purchase_gateway(w).charge(purchase)['balance'] == base - 90
    assert w.api.balance(purchase['accountId'])['balance'] == base - 90


def test_same_id_different_payload_is_quarantined_without_replacing_original(world):
    w = world; purchase = job(); command = purchase_command(purchase);accept(w, command)
    changed = {**command, 'payload': {**command['payload'], 'amount': 9999}}
    accept(w, changed);accept(w, changed)
    assert w.process.once() and purchase_gateway(w).charge(purchase)['status'] == 'APPLIED'
    with w.db.transaction() as session:
        assert session.scalar(select(func.count()).select_from(BrokerRejection)) == 1
        assert session.scalar(select(BrokerRejection)).body_prefix == b''


@pytest.mark.parametrize('amount', [True, 90.0, '90', -1])
def test_invalid_financial_command_never_changes_balance(world, amount):
    w = world; purchase = job(); base = w.api.balance(purchase['accountId'])['balance']; command = purchase_command(purchase)
    command['payload']['amount'] = amount;accept(w, command)
    assert w.process.once()
    assert w.reader.command_status(UUID(command['messageId']))['status'] == 'REJECTED'
    assert w.api.balance(purchase['accountId'])['balance'] == base


def test_game_rewards_use_same_business_receipt_across_http_and_rabbit(world):
    w = world; account, run = uuid4(), uuid4(); base = w.api.balance(account)['balance']
    payload = {'accountId': str(account), 'runId': str(run), 'amount': 25}
    old_receipt = w.api.receive(uuid4(), 'run.currency_reward', 1, payload)['receipt']
    message = {'id': uuid4(), 'destination': 'wallet', 'event_name': 'run.currency_reward', 'schema_version': 1, 'payload': payload}
    gateway = WalletRabbitDelivery(w.publisher, w.reader, None)
    with pytest.raises(GameError):gateway.deliver(message)
    assert w.process.once() and gateway.deliver(message) == old_receipt
    assert w.api.balance(account)['balance'] == base + 25


def test_rpc_outage_still_publishes_to_durable_queue_and_never_confirms_purchase(world):
    w = world; purchase = job();w.server.stop(0).wait()
    with pytest.raises(ShopError):purchase_gateway(w).charge(purchase)
    assert len(w.publisher.sent) == 1 and w.process.once()
    # A wrong service credential is not accepted as a missing command.
    assert w.reader.channel is not None


def test_mismatched_saved_hash_and_review_do_not_settle_a_purchase(world):
    w = world; purchase = job();command = purchase_command(purchase);accept(w, command);w.process.once()
    saved = w.reader.command_status(UUID(command['messageId']))
    reader = SimpleNamespace(command_status=lambda _: {**saved, 'payloadSha256': '0' * 64})
    gateway = WalletBrokerPurchaseGateway(w.publisher, reader, None)
    with pytest.raises(ShopError) as error:gateway.charge(purchase)
    assert error.value.status_code == 503
    saved['status'] = 'REVIEW';saved['result'] = {'messageId': command['messageId'], 'status': 'REVIEW'}
    reader.command_status = lambda _: saved
    with pytest.raises(ParticipantConflict):gateway.charge(purchase)


def test_retry_budget_and_explicit_redrive_preserve_command(world, monkeypatch):
    w = world; purchase = job();command = purchase_command(purchase);accept(w, command)
    original = w.api.receive
    monkeypatch.setattr(w.api, 'receive', lambda *args: (_ for _ in ()).throw(OSError('temporary')))
    for _ in range(20):
        assert w.process.once();expire(w)
    assert not w.process.once()
    assert w.operations.get(UUID(command['messageId']))['status'] == 'REVIEW'
    assert w.operations.replay(UUID(command['messageId']))
    monkeypatch.setattr(w.api, 'receive', original)
    assert w.process.once() and purchase_gateway(w).charge(purchase)['status'] == 'APPLIED'
    assert w.operations.get(UUID(command['messageId']))['deliveryGeneration'] == 2


def test_real_rabbit_input_commit_failure_requeues_and_restart_replays_once(world):
    url = os.getenv('VD_TEST_RABBITMQ_URL')
    if not url:pytest.skip('Set VD_TEST_RABBITMQ_URL for real broker delivery')
    w = world; suffix = uuid4().hex; settings = RabbitSettings(url, 'review.wallet.' + suffix, 'review.' + suffix)
    broker = RabbitBroker(settings)
    try:
        command = purchase_command(job());broker.publish_command(json.dumps(command).encode())
        with w.db.engine.begin() as connection:
            connection.execute(text('ALTER TABLE broker_inbox ADD CONSTRAINT injected_intake CHECK (false)'))
        with pytest.raises(BrokerUnavailable):broker.receive_once(w.accept)
        with w.db.engine.begin() as connection:
            assert connection.scalar(text('SELECT count(*) FROM broker_inbox')) == 0
            connection.execute(text('ALTER TABLE broker_inbox DROP CONSTRAINT injected_intake'))
        assert broker.receive_once(w.accept) and w.process.once()
        broker.publish_command(json.dumps(command).encode());assert broker.receive_once(w.accept)
        assert not w.process.once()
        assert not PublishBrokerEvents(w.uow, broker).once()
        assert w.reader.command_status(UUID(command['messageId']))['status'] == 'APPLIED'
        w.accept.execute(b'not-json-do-not-leak-this')
        assert PublishBrokerEvents(w.uow, broker).once()
        method, _, body = broker.channel.basic_get(settings.dead_queue, auto_ack=True)
        assert method is not None and b'do-not-leak' not in body
    finally:
        broker.connect()
        for queue in (settings.commands_queue, settings.dead_queue):broker.channel.queue_delete(queue=queue)
        for exchange in (settings.commands_exchange, settings.dead_exchange):broker.channel.exchange_delete(exchange=exchange)
        broker.close()


@pytest.mark.parametrize('package', ['vatican_backend', 'vatican_shop'])
def test_real_isolated_producer_process_publishes_to_wallet_queue(world, package):
    import importlib
    url = os.getenv('VD_TEST_RABBITMQ_URL')
    if not url:pytest.skip('Set VD_TEST_RABBITMQ_URL for real producer confirmation')
    suffix = uuid4().hex;settings = RabbitSettings(url, 'review.wallet.' + suffix, 'review.' + suffix)
    broker = RabbitBroker(settings)
    publisher = importlib.import_module(package + '.integrations.wallet_publisher').RabbitWalletPublisher(settings)
    try:
        command = purchase_command(job())
        publisher.publish(command)
        assert broker.receive_once(world.accept) and world.process.once()
        publisher.publish(command)
        assert broker.receive_once(world.accept) and not world.process.once()
        assert world.reader.command_status(UUID(command['messageId']))['status'] == 'APPLIED'
    finally:
        broker.connect()
        for queue in (settings.commands_queue, settings.dead_queue):broker.channel.queue_delete(queue=queue)
        for exchange in (settings.commands_exchange, settings.dead_exchange):broker.channel.exchange_delete(exchange=exchange)
        broker.close()
