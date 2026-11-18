"""Real PostgreSQL, RabbitMQ and gRPC; all state is in disposable test schemas."""
from contextlib import contextmanager
from datetime import datetime, timedelta, timezone
import json
import math
import os
from types import SimpleNamespace
from uuid import UUID, uuid4

import grpc
import httpx
import pytest
from sqlalchemy import func, select

from vatican_inventory.application.broker import AcceptBrokerDelivery, BrokerOperations, ProcessBrokerCommands
from vatican_inventory.application.health import HealthUseCases
from vatican_inventory.application.use_cases import InventoryUseCases
from vatican_inventory.bootstrap.rabbit import RabbitSettings
from vatican_inventory.integrations.rabbit import RabbitBroker
from vatican_inventory.integrations.grpc_contract import inventory_pb2 as pb, inventory_pb2_grpc as rpc
from vatican_inventory.migrate import SCHEMA_REVISION
from vatican_inventory.persistence.postgres.repositories.broker import BrokerRepository
from vatican_inventory.persistence.postgres.tables import BrokerInbox, OwnedWeapon
from vatican_inventory.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as InventoryUow
from vatican_inventory.presentation.grpc import create_server
from vatican_backend.application.errors import ApplicationError
from vatican_backend.application.messaging import DeliverRewards
from vatican_backend.bootstrap.inventory_rabbit import RabbitSettings as GameRabbitSettings
from vatican_backend.domain.inventory_commands import command, result
from vatican_backend.integrations.inventory_grpc import InventoryGrpcClient
from vatican_backend.integrations.inventory_publisher import RabbitInventoryPublisher
from vatican_backend.integrations.inventory_rabbit import InventoryRabbitDelivery
from vatican_backend.persistence.postgres.tables import OutboxMessage as GameMessage
from vatican_backend.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as GameUow
from vatican_shop.application.use_cases import SettlePurchases
from vatican_shop.application.errors import ApplicationError as ShopError
from vatican_shop.bootstrap.settings import Settings as ShopSettings
from vatican_shop.bootstrap.rabbit import RabbitSettings as ShopRabbitSettings
from vatican_shop.integrations.rabbit import RabbitInventoryPublisher as ShopPublisher
from vatican_shop.integrations.inventory_grpc import InventoryGrpcClient as ShopReader
from vatican_shop.integrations.rabbit_purchase import BrokerPurchaseGateway
from vatican_shop.domain.inventory_commands import command_id, grant_command
from vatican_shop.persistence.postgres.tables import PurchaseJob
from .test_settlement import world

SECRET = 's' * 32


@contextmanager
def serve_inventory(api, database, port=0):
    operations = BrokerOperations(api.uow)
    values = {InventoryUseCases: api, BrokerOperations: operations,
              HealthUseCases: HealthUseCases(api.uow, SCHEMA_REVISION)}
    container = SimpleNamespace(get=values.__getitem__)
    server, actual = create_server(container, SimpleNamespace(service_secret=SECRET), '127.0.0.1:' + str(port))
    server.start()
    reader = InventoryGrpcClient('127.0.0.1:' + str(actual), SECRET, timeout=1)
    try:
        yield SimpleNamespace(reader=reader, server=server, port=actual, operations=operations)
    finally:
        reader.close()
        server.stop(0).wait()


@pytest.fixture
def rpc_inventory(world):
    with serve_inventory(world.inventory, world.inventory_db) as value:
        yield value


@pytest.fixture
def rabbit():
    url = os.getenv('VD_TEST_RABBITMQ_URL')
    if not url:pytest.skip('Set VD_TEST_RABBITMQ_URL to an isolated RabbitMQ vhost')
    prefix = 'inventory_link_' + uuid4().hex
    settings = RabbitSettings(url, prefix + '.inventory', prefix)
    broker = RabbitBroker(settings)
    broker.connect()
    try:
        yield broker
    finally:
        broker.connect()
        for queue in (settings.commands_queue, settings.results_queue, settings.dead_queue):
            broker.channel.queue_delete(queue=queue)
        for exchange in (settings.commands_exchange, settings.results_exchange, settings.dead_exchange):
            broker.channel.exchange_delete(exchange=exchange)
        broker.close()


def publisher(rabbit):
    settings = rabbit.settings
    return RabbitInventoryPublisher(GameRabbitSettings(settings.url, settings.queue_prefix, settings.exchange_prefix))


def accept_and_process(api, rabbit):
    receiver = AcceptBrokerDelivery(api.uow)
    while rabbit.receive_once(receiver):pass
    processor = ProcessBrokerCommands(api.uow, api)
    while processor.once():pass


def ready_game(database, identity):
    with database.transaction() as session:
        session.get(GameMessage, identity).available_at = datetime.now(timezone.utc) - timedelta(seconds=1)


def enqueue(world, event, payload):
    identity = uuid4()
    with world.game_db.transaction() as session:
        session.add(GameMessage(id=identity, aggregate_id=uuid4(), destination='inventory',
            event_name=event, schema_version=1, payload=payload))
    return identity


def fallback():
    return SimpleNamespace(deliver=lambda _: pytest.fail('Inventory delivery must not use HTTP'))


def worker(world, rpc_inventory, rabbit):
    class Reader:
        def command_status(self, identity):
            assert world.game_db.engine.pool.checkedout() == 0
            return rpc_inventory.reader.command_status(identity)
    return DeliverRewards(GameUow(world.game_db), InventoryRabbitDelivery(publisher(rabbit), Reader(), fallback()))


def test_grpc_auth_not_found_validation_and_lossless_snapshot(world, rpc_inventory):
    r = rpc_inventory
    with grpc.insecure_channel('127.0.0.1:' + str(r.port)) as channel:
        stub = rpc.InventoryReadsStub(channel)
        assert stub.CheckReady(pb.ReadyRequest(), timeout=1).schema_revision == SCHEMA_REVISION
        for metadata in ((), (('x-service-secret', 'wrong'),), (('x-service-secret', SECRET), ('x-service-secret', SECRET))):
            with pytest.raises(grpc.RpcError) as error:
                stub.GetSnapshot(pb.AccountRequest(account_id=str(world.account_id)), timeout=1, metadata=metadata)
            assert error.value.code() == grpc.StatusCode.UNAUTHENTICATED
        with pytest.raises(grpc.RpcError) as error:
            stub.GetCommandStatus(pb.CommandRequest(message_id='not-a-uuid'), timeout=1, metadata=(('x-service-secret', SECRET),))
        assert error.value.code() == grpc.StatusCode.INVALID_ARGUMENT
    assert r.reader.command_status(uuid4()) is None
    snapshot = r.reader.snapshot(world.account_id)
    assert snapshot == world.inventory.get(world.account_id, selected_only=True)
    # Additional content fields retain int64/float/negative-zero values instead of Struct doubles.
    original = world.inventory.definitions
    definitions = original.get()
    definitions[0].update(Exact=2**63-1, Damage=1e30, SignedZero=-0.0)
    world.inventory.definitions = SimpleNamespace(get=lambda: definitions)
    try:
        definition = r.reader.snapshot(world.account_id)['Configurations'][0]
        assert type(definition['Exact']) is int and definition['Exact'] == 2**63-1
        assert definition['Damage'] == 1e30 and math.copysign(1, definition['SignedZero']) == -1
    finally:world.inventory.definitions = original


def test_grpc_deadline_and_safe_errors(world, rpc_inventory, monkeypatch):
    import time
    original = world.inventory.get
    def slow(*args, **kwargs):
        time.sleep(.15)
        return original(*args, **kwargs)
    monkeypatch.setattr(world.inventory, 'get', slow)
    reader = InventoryGrpcClient('127.0.0.1:' + str(rpc_inventory.port), SECRET, timeout=.02)
    try:
        with pytest.raises(ApplicationError) as error:reader.snapshot(world.account_id)
        assert error.value.status_code == 503 and 'PRIVATE' not in error.value.detail
        time.sleep(.2)  # Let the cancelled server call finish before fixture teardown.
    finally:reader.close()
    def broken(*args, **kwargs):raise RuntimeError('PRIVATE SQL/password')
    monkeypatch.setattr(world.inventory, 'get', broken)
    with grpc.insecure_channel('127.0.0.1:' + str(rpc_inventory.port)) as channel:
        with pytest.raises(grpc.RpcError) as error:
            rpc.InventoryReadsStub(channel).GetSnapshot(pb.AccountRequest(account_id=str(world.account_id)),
                timeout=1, metadata=(('x-service-secret', SECRET),))
        assert error.value.code() == grpc.StatusCode.INTERNAL and 'PRIVATE' not in error.value.details()


def test_game_run_reward_confirm_loss_and_inventory_crash_do_not_repeat_xp(world, rpc_inventory, rabbit, monkeypatch):
    snapshot = rpc_inventory.reader.snapshot(world.account_id)
    identity = enqueue(world, 'run.weapon_reward', {'accountId': str(world.account_id), 'runId': str(uuid4()),
        'configurationId': 'sword.common', 'snapshot': snapshot})
    delivery = worker(world, rpc_inventory, rabbit)
    assert delivery.once()  # Confirmed publication is not a business confirmation.
    with world.game_db.transaction() as session:assert session.get(GameMessage, identity).published_at is None
    assert rabbit.receive_once(AcceptBrokerDelivery(world.inventory.uow))
    with monkeypatch.context() as patch:
        def lost(*args, **kwargs):raise RuntimeError('Lost final inbox commit after inventory business commit')
        patch.setattr(BrokerRepository, 'finish_command', lost)
        with pytest.raises(RuntimeError):ProcessBrokerCommands(world.inventory.uow, world.inventory).once()
    first = world.inventory.get(world.account_id)
    assert len(first['Weapons']) == 2
    with world.inventory_db.transaction() as session:
        session.get(BrokerInbox, identity).available_at = datetime.now(timezone.utc) - timedelta(seconds=1)
    assert ProcessBrokerCommands(world.inventory.uow, world.inventory).once()  # A new processor recovers the lease.
    assert world.inventory.get(world.account_id) == first
    original = rpc_inventory.reader.command_status
    monkeypatch.setattr(rpc_inventory.reader, 'command_status', lambda _: (_ for _ in ()).throw(ApplicationError(503, 'Lost RPC response')))
    ready_game(world.game_db, identity);assert delivery.once()
    monkeypatch.setattr(rpc_inventory.reader, 'command_status', original)
    ready_game(world.game_db, identity);assert delivery.once()
    with world.game_db.transaction() as session:
        row = session.get(GameMessage, identity)
        assert row.published_at and row.receipt_json['runId']
    assert world.inventory.get(world.account_id) == first


@pytest.mark.parametrize('event,kind', [('level.reward', 'ITEM'), ('case.reward', 'CASE')])
def test_game_reward_commands_reconcile_over_grpc(world, rpc_inventory, rabbit, event, kind):
    payload = {'accountId': str(world.account_id), 'claimId' if event == 'level.reward' else 'openingId': str(uuid4()),
               'itemIndex': 0, 'rewardType': kind, 'rewardRef': 'test', 'quantity': 3}
    identity = enqueue(world, event, payload)
    delivery = worker(world, rpc_inventory, rabbit)
    assert delivery.once();accept_and_process(world.inventory, rabbit)
    ready_game(world.game_db, identity);assert delivery.once()
    with world.game_db.transaction() as session:assert session.get(GameMessage, identity).published_at
    assert world.inventory.items(world.account_id)['items'][0]['quantity'] == 3


def test_game_item_debit_and_duplicate_queue_delivery(world, rpc_inventory, rabbit):
    world.inventory.receive(uuid4(), 'level.reward', 1, {'accountId': str(world.account_id), 'claimId': str(uuid4()),
        'itemIndex': 0, 'rewardType': 'ITEM', 'rewardRef': 'kit', 'quantity': 4})
    identity = enqueue(world, 'inventory.item.consume', {'accountId': str(world.account_id), 'operationId': str(uuid4()),
        'itemId': 'item:kit', 'quantity': 3})
    delivery = worker(world, rpc_inventory, rabbit)
    assert delivery.once()
    with world.game_db.transaction() as session:
        row = session.get(GameMessage, identity)
        envelope = {'messageId': str(identity), 'eventName': row.event_name, 'schemaVersion': 1, 'payload': row.payload}
    for _ in range(4):publisher(rabbit).publish(envelope)
    accept_and_process(world.inventory, rabbit)
    ready_game(world.game_db, identity);assert delivery.once()
    assert world.inventory.item(world.account_id, 'item:kit')['quantity'] == 1
    with world.inventory_db.transaction() as session:assert session.scalar(select(func.count()).select_from(BrokerInbox)) == 1


def test_invalid_grpc_ack_never_completes_game_delivery(world, rpc_inventory, rabbit):
    payload = {'accountId': str(world.account_id), 'claimId': str(uuid4()), 'itemIndex': 0,
               'rewardType': 'ITEM', 'rewardRef': 'kit', 'quantity': 1}
    identity = enqueue(world, 'level.reward', payload)
    delivery = worker(world, rpc_inventory, rabbit)
    delivery.once();accept_and_process(world.inventory, rabbit)
    saved = rpc_inventory.reader.command_status(identity)
    message = {'id': identity, 'destination': 'inventory', 'event_name': 'level.reward', 'schema_version': 1, 'payload': payload}
    for body in ({**saved, 'payloadSha256': '0'*64}, {**saved, 'deliveryGeneration': True},
                 {**saved, 'result': {**saved['result'], 'receipt': {**saved['result']['receipt'], 'quantity': True}}}):
        with pytest.raises(ValueError):result(message, body)


def test_rpc_server_restart_recovers_an_already_applied_game_reward(world, rpc_inventory, rabbit):
    payload = {'accountId': str(world.account_id), 'claimId': str(uuid4()), 'itemIndex': 0,
               'rewardType': 'ITEM', 'rewardRef': 'restart', 'quantity': 2}
    identity = enqueue(world, 'level.reward', payload)
    delivery = worker(world, rpc_inventory, rabbit)
    delivery.once();accept_and_process(world.inventory, rabbit)
    rpc_inventory.server.stop(0).wait()
    ready_game(world.game_db, identity);assert delivery.once()
    with world.game_db.transaction() as session:assert session.get(GameMessage, identity).published_at is None
    with serve_inventory(world.inventory, world.inventory_db, rpc_inventory.port) as restarted:
        grpc.channel_ready_future(restarted.reader.channel).result(timeout=5)
        rpc_inventory.reader = restarted.reader
        ready_game(world.game_db, identity);assert delivery.once()
        with world.game_db.transaction() as session:assert session.get(GameMessage, identity).published_at
        assert world.inventory.item(world.account_id, 'item:restart')['quantity'] == 2


def test_lost_amqp_confirm_republishes_same_source_and_applies_once(world, rpc_inventory, rabbit):
    payload = {'accountId': str(world.account_id), 'claimId': str(uuid4()), 'itemIndex': 0,
               'rewardType': 'ITEM', 'rewardRef': 'confirm', 'quantity': 2}
    identity = enqueue(world, 'level.reward', payload)
    real = publisher(rabbit)
    published = []
    class LostConfirm:
        def publish(self, value):
            real.publish(value);published.append(value)
            raise ApplicationError(503, 'Lost confirm after durable publish')
    sender = DeliverRewards(GameUow(world.game_db), InventoryRabbitDelivery(LostConfirm(), rpc_inventory.reader, fallback()))
    assert sender.once()
    ready_game(world.game_db, identity);assert sender.once()
    assert len(published) == 2 and published[0] == published[1] and published[0]['messageId'] == str(identity)
    accept_and_process(world.inventory, rabbit)
    ready_game(world.game_db, identity);assert sender.once()
    with world.game_db.transaction() as session:assert session.get(GameMessage, identity).published_at
    assert world.inventory.item(world.account_id, 'item:confirm')['quantity'] == 2


def test_shop_grant_uses_mq_and_grpc_and_preserves_purchase_on_rpc_outage(world, rpc_inventory, rabbit):
    from vatican_shop.application.use_cases import ShopUseCases
    from vatican_shop.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as ShopUow
    catalog = SimpleNamespace(get=lambda: {'version': 'v1', 'offers': [{'id': 'test', 'weaponConfigurationId': 'sword.common',
        'price': 25, 'purchaseLimit': 0, 'available': True}]})
    uow = ShopUow(world.shop_db);api = ShopUseCases(uow, catalog)
    operation = uuid4();api.purchase(world.account_id, operation, 'test', 'v1')
    def http(request):
        assert world.shop_db.engine.pool.checkedout() == 0
        assert request.url.path in ('/internal/charges', '/internal/refunds'), 'No inventory HTTP calls allowed'
        value = json.loads(request.content)
        receipt = world.wallet.change(UUID(value['accountId']), UUID(value['operationId']), value['amount'],
            refund=request.url.path == '/internal/refunds')
        return httpx.Response(200, json=receipt)
    settings = ShopSettings('unused', SECRET)
    reader = ShopReader('127.0.0.1:' + str(rpc_inventory.port), SECRET)
    config = rabbit.settings
    pub = ShopPublisher(ShopRabbitSettings(config.url, config.queue_prefix, config.exchange_prefix))
    with httpx.Client(transport=httpx.MockTransport(http)) as client:
        gateway = BrokerPurchaseGateway(client, settings, pub, reader)
        settle = SettlePurchases(uow, gateway)
        assert settle.once() and settle.once()
        assert api.receipt(world.account_id, operation)['status'] == 'Pending'
        accept_and_process(world.inventory, rabbit)
        original = reader.command_status
        reader.command_status = lambda _: (_ for _ in ()).throw(ShopError(503, 'RPC outage'))
        def ready():
            with world.shop_db.transaction() as session:
                session.get(PurchaseJob, (world.account_id, operation)).available_at = datetime.now(timezone.utc) - timedelta(seconds=1)
        ready();assert settle.once()
        assert api.receipt(world.account_id, operation)['status'] == 'Pending'
        reader.command_status = original
        ready();assert settle.once()
        assert api.receipt(world.account_id, operation)['status'] == 'Completed'
        assert world.wallet.balance(world.account_id)['balance'] == 475
        assert len(world.inventory.get(world.account_id)['Weapons']) == 2
    reader.close()
