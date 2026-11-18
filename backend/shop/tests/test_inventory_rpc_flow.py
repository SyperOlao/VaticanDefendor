from datetime import datetime, timedelta, timezone
import json
from uuid import UUID, uuid4

import httpx
import pytest
from sqlalchemy import func, select

from vatican_inventory.application.broker import AcceptBrokerDelivery, BrokerOperations, ProcessBrokerCommands
from vatican_inventory.application.errors import ApplicationError as InventoryError
from vatican_inventory.persistence.postgres.repositories.broker import BrokerRepository
from vatican_inventory.persistence.postgres.tables import BrokerInbox
from vatican_inventory.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as InventoryUow
from vatican_shop.application.errors import ApplicationError
from vatican_shop.application.models import Principal
from vatican_shop.application.operations import AdminPurchases
from vatican_shop.application.use_cases import SettlePurchases
from vatican_shop.bootstrap.settings import Settings
from vatican_shop.domain.inventory_commands import command_id
from vatican_shop.integrations.rabbit_purchase import BrokerPurchaseGateway
from vatican_shop.persistence.postgres.tables import OutboxMessage, PurchaseJob
from .test_purchases import buy, drain, ready, weapon_count, world


@pytest.fixture
def rpc(world, shop_storage):
    w = world
    inventory_uow = InventoryUow(w.inventory_db)
    operations = BrokerOperations(inventory_uow)
    receiver = AcceptBrokerDelivery(inventory_uow)
    processor = ProcessBrokerCommands(inventory_uow, w.inventory)
    class Publisher:
        def __init__(self):
            self.commands=[];self.queue=[];self.lose_confirm=False
        def publish(self, command):
            assert w.database.engine.pool.checkedout()==0
            self.commands.append(command);self.queue.append(command)
            if self.lose_confirm:raise ApplicationError(503,"Private confirm lost")
        def deliver(self):
            while self.queue:
                value=self.queue.pop(0)
                receiver.execute(json.dumps(value).encode(),value['messageId'],'application/json','utf-8')
    publisher=Publisher()
    def handler(request):
        assert w.database.engine.pool.checkedout()==0
        assert request.headers['X-Service-Secret']=='s'*32
        if request.method=='GET':
            identity=UUID(request.url.path.rsplit('/',1)[-1])
            try:return httpx.Response(200,json=operations.get(identity))
            except InventoryError as error:return httpx.Response(error.status_code,json={"code":"Unavailable"})
        assert request.url.path in ('/internal/charges','/internal/refunds')
        body=json.loads(request.content)
        receipt=w.wallet.change(UUID(body['accountId']),UUID(body['operationId']),body['amount'],
                                refund=request.url.path=='/internal/refunds')
        return httpx.Response(200,json=receipt)
    client=httpx.Client(transport=httpx.MockTransport(handler))
    gateway=BrokerPurchaseGateway(client,Settings(shop_storage[1],'s'*32),publisher)
    original_worker=w.worker
    w.worker=SettlePurchases(w.uow,gateway)
    w.publisher,w.processor,w.receiver,w.operations=publisher,processor,receiver,operations
    w.original_worker=original_worker
    yield w
    client.close()


def sent(w):
    operation,_=buy(w)
    assert w.worker.once() and w.worker.once()
    assert w.api.receipt(w.account,operation)['status']=='Pending'
    assert w.wallet.balance(w.account)['balance']==475
    return operation


def finish(w,operation):
    w.publisher.deliver()
    assert w.processor.once()
    assert not w.processor.once()
    ready(w,operation);drain(w)
    assert w.api.receipt(w.account,operation)['status']=='Completed'
    assert w.wallet.balance(w.account)['balance']==475 and weapon_count(w)==1
    with w.database.transaction() as session:
        assert session.scalar(select(func.count()).select_from(OutboxMessage))==1


def test_confirmed_publication_is_not_a_completed_purchase(rpc):
    w=rpc;operation=sent(w)
    assert weapon_count(w)==0
    finish(w,operation)
    assert w.api.receipt(w.account,operation)['inventoryRevision']>=2
    assert len(w.publisher.commands)==1


def test_lost_confirms_and_twenty_duplicate_deliveries_issue_one_weapon(rpc):
    w=rpc;w.publisher.lose_confirm=True
    operation=sent(w)
    for _ in range(5):
        ready(w,operation);assert w.worker.once()
    assert len(w.publisher.commands)==6
    assert len({c['messageId'] for c in w.publisher.commands})==1
    value=w.publisher.commands[0]
    for _ in range(20):w.receiver.execute(json.dumps(value).encode(),value['messageId'],'application/json','utf-8')
    finish(w,operation)
    with w.inventory_db.transaction() as session:
        assert session.scalar(select(func.count()).select_from(BrokerInbox))==1


def test_pending_inbox_suppresses_republication_and_honors_purchase_backoff(rpc):
    w=rpc;operation=sent(w);w.publisher.deliver()
    assert not w.worker.once()
    for _ in range(4):
        ready(w,operation);assert w.worker.once()
    assert len(w.publisher.commands)==1 and weapon_count(w)==0
    assert w.processor.once()
    ready(w,operation);drain(w)
    assert w.api.receipt(w.account,operation)['status']=='Completed' and weapon_count(w)==1


def test_saved_grant_rejection_refunds_only_after_inventory_confirmation(rpc):
    w=rpc;operation=sent(w);w.definitions.ids=['sword.common']
    w.publisher.deliver();assert w.processor.once()
    ready(w,operation);assert w.worker.once()
    with w.database.transaction() as session:
        assert session.get(PurchaseJob,(w.account,operation)).state=='REFUND_PENDING'
    assert w.wallet.balance(w.account)['balance']==475
    drain(w)
    assert w.api.receipt(w.account,operation)['code']=='GrantRejected'
    assert w.wallet.balance(w.account)['balance']==500 and weapon_count(w)==0


def test_inventory_review_and_explicit_redrive_preserve_original_source(rpc):
    w=rpc;operation=sent(w);w.publisher.deliver();w.definitions.unavailable=True
    identity=command_id(w.account,operation)
    with w.inventory_db.transaction() as session:session.get(BrokerInbox,identity).attempts=19
    assert w.processor.once()
    ready(w,operation);assert w.worker.once()
    assert w.api.receipt(w.account,operation)['status']=='Review'
    assert w.wallet.balance(w.account)['balance']==475 and weapon_count(w)==0
    w.definitions.unavailable=False
    assert w.operations.replay(identity)
    assert w.processor.once()
    class Auth:
        def authenticate(self,token):return Principal(uuid4(),False,('ADMIN',))
    admin=AdminPurchases(w.uow,Auth());row=admin.get('admin',w.account,operation)
    admin.retry('admin',w.account,operation,row['job']['version'],uuid4(),'Inventory redrive checked')
    drain(w)
    assert w.api.receipt(w.account,operation)['status']=='Completed'
    assert len(w.publisher.commands)==1 and weapon_count(w)==1 and w.wallet.balance(w.account)['balance']==475
    assert w.operations.get(identity)['deliveryGeneration']==2


def test_inventory_commit_before_lost_finish_ack_replays_source_receipt(rpc,monkeypatch):
    w=rpc;operation=sent(w);w.publisher.deliver()
    with monkeypatch.context() as patch:
        def fail(*args,**kwargs):raise RuntimeError('Private DB failure after business commit')
        patch.setattr(BrokerRepository,'finish_command',fail)
        with pytest.raises(RuntimeError):w.processor.once()
    assert weapon_count(w)==1 and w.api.receipt(w.account,operation)['status']=='Pending'
    with w.inventory_db.transaction() as session:
        session.get(BrokerInbox,command_id(w.account,operation)).available_at=datetime.now(timezone.utc)-timedelta(seconds=1)
    assert w.processor.once()
    ready(w,operation);drain(w)
    assert w.api.receipt(w.account,operation)['status']=='Completed'
    assert weapon_count(w)==1 and w.wallet.balance(w.account)['balance']==475


def test_switch_to_rabbit_after_lost_http_grant_ack_does_not_grant_again(rpc):
    w=rpc;w.gateway.lost='grant'
    operation,_=buy(w);assert w.original_worker.once() and w.original_worker.once()
    assert weapon_count(w)==1
    w.definitions.unavailable=True
    ready(w,operation);assert w.worker.once()
    finish(w,operation)


def test_switch_to_http_with_an_outstanding_broker_command_keeps_one_source(rpc):
    w=rpc;operation=sent(w)
    ready(w,operation);assert w.original_worker.once()
    assert w.api.receipt(w.account,operation)['status']=='Completed'
    w.publisher.deliver();assert w.processor.once()
    assert weapon_count(w)==1 and w.wallet.balance(w.account)['balance']==475
    assert not w.worker.once()


def test_same_operation_uuid_for_different_accounts_has_distinct_commands(rpc):
    w=rpc;operation=uuid4();other=uuid4()
    for account in (w.account,other):w.api.purchase(account,operation,'sword','v1')
    drain(w)
    assert len(w.publisher.commands)==2 and len({c['messageId'] for c in w.publisher.commands})==2
    w.publisher.deliver();assert w.processor.once() and w.processor.once()
    with w.database.transaction() as session:
        for job in session.scalars(select(PurchaseJob)):job.available_at=datetime.now(timezone.utc)-timedelta(seconds=1)
    drain(w)
    assert all(w.api.receipt(account,operation)['status']=='Completed' for account in (w.account,other))
    assert all(w.wallet.balance(account)['balance']==475 for account in (w.account,other))
