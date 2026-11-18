"""Real RabbitMQ command delivery; each test owns its queues and exchanges."""
import os
import time
from uuid import uuid4

import pytest
from sqlalchemy import func, select

from vatican_inventory.bootstrap.rabbit import RabbitSettings as InventoryRabbitSettings
from vatican_inventory.integrations.rabbit import BrokerUnavailable, RabbitBroker
from vatican_inventory.persistence.postgres.tables import BrokerInbox
from vatican_shop.bootstrap.rabbit import RabbitSettings
from vatican_shop.domain.inventory_commands import command_id
from vatican_shop.application.models import Principal
from vatican_shop.application.operations import AdminPurchases
from vatican_shop.integrations.rabbit import RabbitInventoryPublisher
from vatican_shop.persistence.postgres.repositories.state import StateRepository
from .test_inventory_rpc_flow import rpc, world
from .test_purchases import buy, drain, ready, weapon_count


@pytest.fixture
def live(rpc):
    url=os.getenv('VD_TEST_RABBITMQ_URL')
    if not url:pytest.skip('Set VD_TEST_RABBITMQ_URL to isolated RabbitMQ 4.1+ for shop RPC tests')
    prefix='shop_rpc_test_'+uuid4().hex
    settings=InventoryRabbitSettings(url,prefix+'.inventory',prefix)
    broker=RabbitBroker(settings)
    try:
        broker.connect()
        publisher=RabbitInventoryPublisher(RabbitSettings(url,settings.queue_prefix,settings.exchange_prefix))
        rpc.worker.gateway.publisher=publisher
        yield rpc,broker,publisher
    finally:
        try:
            broker.connect()
            for queue in (settings.commands_queue,settings.results_queue,settings.dead_queue):
                broker.channel.queue_delete(queue=queue)
            for exchange in (settings.commands_exchange,settings.results_exchange,settings.dead_exchange):
                broker.channel.exchange_delete(exchange=exchange)
        finally:broker.close()


def accept_and_process(w,broker):
    deadline=time.monotonic()+10
    while not broker.receive_once(w.receiver):
        assert time.monotonic()<deadline,'Confirmed command was not delivered'
        time.sleep(0.05)
    while broker.receive_once(w.receiver):pass
    assert w.processor.once()
    assert not w.processor.once()


def completed(w,operation):
    ready(w,operation);drain(w)
    receipt=w.api.receipt(w.account,operation)
    assert receipt['status']=='Completed' and receipt['inventoryRevision']>=2
    assert w.wallet.balance(w.account)['balance']==475 and weapon_count(w)==1
    with w.inventory_db.transaction() as session:
        assert session.scalar(select(func.count()).select_from(BrokerInbox))==1


def test_real_rabbit_request_and_durable_result_complete_without_result_queue_consumer(live):
    w,broker,_=live;operation,_=buy(w)
    assert w.worker.once() and w.worker.once()
    assert w.api.receipt(w.account,operation)['status']=='Pending' and weapon_count(w)==0
    accept_and_process(w,broker)
    completed(w,operation)
    # Shop reads the committed inbox receipt; pending inventory result publications are independent.
    result=w.operations.get(command_id(w.account,operation))
    assert result['status']=='APPLIED' and all(event['status']=='PENDING' for event in result['events'])


def test_lost_real_publisher_confirms_repeat_one_command_and_one_business_effect(live):
    w,broker,publisher=live
    class LoseConfirm:
        def publish(self,command):
            publisher.publish(command)
            raise RuntimeError('Private acknowledgement lost after broker accepted publication')
    w.worker.gateway.publisher=LoseConfirm()
    operation,_=buy(w);assert w.worker.once()
    for _ in range(6):
        ready(w,operation);assert w.worker.once()
    accept_and_process(w,broker)
    completed(w,operation)


def test_unavailable_broker_does_not_refund_or_issue_a_new_source(live):
    w,broker,publisher=live
    # A closed loopback port creates an actual connection failure without stopping other tests' brokers.
    w.worker.gateway.publisher=RabbitInventoryPublisher(RabbitSettings('amqp://test:test@127.0.0.1:1/%2F'))
    operation,_=buy(w);assert w.worker.once() and w.worker.once()
    assert w.wallet.balance(w.account)['balance']==475 and weapon_count(w)==0
    assert w.api.receipt(w.account,operation)['status']=='Pending'
    w.worker.gateway.publisher=publisher
    ready(w,operation);assert w.worker.once()
    accept_and_process(w,broker)
    completed(w,operation)


def test_purchase_commit_failure_after_real_publish_recovers_from_the_original_job(live,monkeypatch):
    w,broker,_=live;operation,_=buy(w);assert w.worker.once()
    with monkeypatch.context() as patch:
        def fail(*args,**kwargs):raise RuntimeError('Private shop commit failure after publish')
        patch.setattr(StateRepository,'transition',fail)
        with pytest.raises(RuntimeError):w.worker.once()
    accept_and_process(w,broker)
    completed(w,operation)


def test_inventory_db_commit_before_rabbit_ack_can_be_redelivered_safely(live):
    w,broker,_=live;operation,_=buy(w)
    assert w.worker.once() and w.worker.once()
    class LoseAck:
        def execute(self,*args):
            w.receiver.execute(*args)
            raise RuntimeError('Private failure between inbox commit and AMQP ack')
    with pytest.raises(BrokerUnavailable):broker.receive_once(LoseAck())
    accept_and_process(w,broker)
    completed(w,operation)


def test_confirmed_business_rejection_over_real_rabbit_returns_original_charge(live):
    w,broker,_=live;operation,_=buy(w)
    assert w.worker.once() and w.worker.once()
    w.definitions.ids=['sword.common']
    accept_and_process(w,broker)
    ready(w,operation);drain(w)
    assert w.api.receipt(w.account,operation)['code']=='GrantRejected'
    assert w.wallet.balance(w.account)['balance']==500 and weapon_count(w)==0


def test_twenty_unconfirmed_publications_and_late_grant_require_explicit_admin_retry(live):
    w,broker,_=live;operation,_=buy(w);assert w.worker.once()
    for _ in range(20):
        ready(w,operation);assert w.worker.once()
    assert w.api.receipt(w.account,operation)['status']=='Review'
    assert w.wallet.balance(w.account)['balance']==475 and weapon_count(w)==0
    accept_and_process(w,broker)
    assert weapon_count(w)==1
    assert not w.worker.once()  # A late result must not reopen an operator-owned Review decision.
    actor=uuid4()
    class Auth:
        def authenticate(self,token):return Principal(actor,False,('ADMIN',))
    admin=AdminPurchases(w.uow,Auth());row=admin.get('admin',w.account,operation)
    admin.retry('admin',w.account,operation,row['job']['version'],uuid4(),'Late inventory result verified')
    drain(w)
    assert w.api.receipt(w.account,operation)['status']=='Completed'
    assert w.wallet.balance(w.account)['balance']==475 and weapon_count(w)==1
