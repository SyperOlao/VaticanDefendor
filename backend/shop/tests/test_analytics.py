from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager
from datetime import datetime, timedelta, timezone
from threading import local
from uuid import UUID, uuid4
import pytest
from sqlalchemy import func, select, text
from sqlalchemy.exc import IntegrityError
from vatican_shop.application.analytics import DeliverAnalytics
from vatican_shop.application.errors import ApplicationError
from vatican_shop.domain.analytics import PURCHASE_EVENT, purchase_id, message_id, expected_receipt
from vatican_shop.persistence.postgres.tables import OutboxMessage, PurchaseJob, PurchaseOperation
from .test_purchases import world, buy, drain, weapon_count


@pytest.fixture
def analytics_world(world,analytics_actor):
    world.analytics=analytics_actor;active=local();original=world.database.transaction
    @contextmanager
    def tracked():
        active.depth=getattr(active,'depth',0)+1
        try:
            with original() as session:yield session
        finally:active.depth-=1
    world.database.transaction=tracked
    world.active=active
    return world


class Gateway:
    def __init__(self,w,mode='normal'):self.w,self.mode=w,mode
    def deliver(self,message):
        assert getattr(self.w.active,'depth',0)==0
        if self.mode=='down':raise ApplicationError(503,'Analytics unavailable')
        if self.mode=='reject':raise ApplicationError(422,'Rejected payload')
        actor=self.w.analytics.api if self.mode=='async' else self.w.analytics
        result=actor.receive(message['id'],message['event_name'],message['schema_version'],message['payload'])
        if result['status']=='PENDING':raise ApplicationError(503,'Projection pending')
        if result['status']=='REVIEW':raise ApplicationError(422,'Projection requires review')
        if self.mode=='lost':raise ApplicationError(503,'Acknowledgement lost after projection')
        receipt=result['receipt']
        return {**receipt,'payloadHash':'0'*64} if self.mode=='wrong' else receipt


def sender(w,mode='normal'):return DeliverAnalytics(w.uow,Gateway(w,mode))


def send(w,mode='normal'):
    for _ in range(30):
        if not sender(w,mode).once():return
    pytest.fail('Analytics did not quiesce')


def ready(w):
    with w.database.transaction() as session:
        for row in session.scalars(select(OutboxMessage).where(OutboxMessage.published_at.is_(None))):
            row.available_at=datetime.now(timezone.utc)-timedelta(seconds=1)
        for row in session.scalars(select(PurchaseJob).where(PurchaseJob.state.in_(['PENDING','CHARGED','REFUND_PENDING']))):
            row.available_at=datetime.now(timezone.utc)-timedelta(seconds=1)


def test_confirmed_purchase_atomically_emits_frozen_analytics_and_api_history_status(analytics_world):
    w=analytics_world;operation,first=buy(w)
    assert first['analyticsStatus']=='SKIPPED'
    assert w.worker.once()  # Only the charge is confirmed.
    assert w.api.receipt(w.account,operation)['analyticsStatus']=='SKIPPED'
    w.catalog.price=400;w.catalog.unavailable=True
    assert w.worker.once()
    completed=w.api.receipt(w.account,operation)
    assert completed['status']=='Completed' and completed['analyticsStatus']=='PENDING'
    with w.database.transaction() as session:
        event=session.get(OutboxMessage,message_id(w.account,operation));payload=event.payload
        assert payload['chargedAmount']==25 and payload['weaponConfigurationId']=='sword.rare'
        assert payload['weaponInstanceIds']==completed['grantedWeaponInstanceIds']
        assert payload['completedAt']==completed['completedAt']
        assert set(payload)=={'purchaseId','operationId','accountId','offerId','catalogVersion','currencyId','chargedAmount',
            'weaponConfigurationId','weaponInstanceIds','status','completedAt'}
    send(w)
    receipt=w.api.receipt(w.account,operation)
    assert receipt['analyticsStatus']=='COMPLETED' and receipt['analytics']['receipt']==expected_receipt(payload)
    assert w.api.purchase(w.account,operation,'sword','v1')['replayed']
    assert w.api.history(w.account)['purchases'][0]['analyticsStatus']=='COMPLETED'
    assert w.wallet.balance(w.account)['balance']==475 and weapon_count(w)==1


@pytest.mark.parametrize('price',[0,25])
def test_analytics_outage_does_not_hold_completed_purchase_or_new_purchases(analytics_world,price):
    w=analytics_world;w.catalog.price=price
    operations=[]
    for _ in range(2):
        operation,_=buy(w);operations.append(operation);drain(w);send(w,'down')
        assert w.api.receipt(w.account,operation)['status']=='Completed'
        assert w.api.receipt(w.account,operation)['analyticsStatus']=='PENDING'
    money=w.wallet.balance(w.account);owned=w.inventory.get(w.account)
    ready(w);send(w)
    assert all(w.api.receipt(w.account,op)['analyticsStatus']=='COMPLETED' for op in operations)
    assert w.wallet.balance(w.account)==money and w.inventory.get(w.account)==owned
    assert len(w.analytics.projection.tables['fact_shop_purchases'])==2


@pytest.mark.parametrize('mode',['lost','wrong','async'])
def test_lost_or_bad_ack_and_pending_inbox_never_regrant_or_repeat_projection(analytics_world,mode):
    w=analytics_world;operation,_=buy(w);drain(w);send(w,mode)
    assert w.api.receipt(w.account,operation)['analyticsStatus']=='PENDING'
    money=w.wallet.balance(w.account);owned=w.inventory.get(w.account)
    if mode=='async':assert w.analytics.worker.once()
    calls=w.analytics.projection.calls
    ready(w);send(w)
    assert w.api.receipt(w.account,operation)['analyticsStatus']=='COMPLETED'
    assert w.analytics.projection.calls==calls
    assert w.wallet.balance(w.account)==money and w.inventory.get(w.account)==owned


@pytest.mark.parametrize('mode',['down','reject'])
def test_review_replay_is_independent_from_purchase_reconciliation(analytics_world,mode):
    w=analytics_world;operation,_=buy(w);drain(w)
    if mode=='down':
        with w.database.transaction() as session:session.get(OutboxMessage,message_id(w.account,operation)).attempts=19
    send(w,mode)
    receipt=w.api.receipt(w.account,operation)
    assert receipt['status']=='Completed' and receipt['analyticsStatus']=='REVIEW'
    assert not w.worker.requeue(w.account,operation)
    assert sender(w).requeue(w.account,operation)
    send(w)
    assert w.api.receipt(w.account,operation)['analyticsStatus']=='COMPLETED' and weapon_count(w)==1
    assert w.wallet.balance(w.account)['balance']==475


def test_completion_commit_failure_rolls_back_outbox_with_purchase_receipt(analytics_world):
    w=analytics_world;operation,_=buy(w);w.worker.once()
    with w.database.engine.begin() as conn:
        conn.execute(text("ALTER TABLE outbox_messages ADD CONSTRAINT injected_purchase_event CHECK (event_name <> 'shop.purchase.analytics')"))
    try:
        with pytest.raises(IntegrityError):w.worker.once()
        assert w.api.receipt(w.account,operation)['status']=='Pending'
        with w.database.transaction() as session:assert session.get(OutboxMessage,message_id(w.account,operation)) is None
        assert weapon_count(w)==1
    finally:
        with w.database.engine.begin() as conn:conn.execute(text('ALTER TABLE outbox_messages DROP CONSTRAINT injected_purchase_event'))
    ready(w);drain(w);send(w)
    assert w.api.receipt(w.account,operation)['analyticsStatus']=='COMPLETED' and weapon_count(w)==1
    assert w.wallet.balance(w.account)['balance']==475


def test_sender_confirmation_failure_preserves_completed_purchase_and_projected_fact(analytics_world):
    w=analytics_world;operation,_=buy(w);drain(w)
    with w.database.engine.begin() as conn:
        conn.execute(text("ALTER TABLE outbox_messages ADD CONSTRAINT injected_analytics_ack CHECK (published_at IS NULL)"))
    try:
        with pytest.raises(IntegrityError):send(w)
        calls=w.analytics.projection.calls
        assert w.api.receipt(w.account,operation)['status']=='Completed'
    finally:
        with w.database.engine.begin() as conn:conn.execute(text('ALTER TABLE outbox_messages DROP CONSTRAINT injected_analytics_ack'))
    ready(w);send(w)
    assert w.api.receipt(w.account,operation)['analyticsStatus']=='COMPLETED' and w.analytics.projection.calls==calls


@pytest.mark.parametrize('already_projected',[False,True])
def test_expired_final_analytics_lease_requires_review_without_another_send(analytics_world,already_projected):
    w=analytics_world;operation,_=buy(w);drain(w)
    if already_projected:
        with w.uow() as work:claimed=work.repository.claim_analytics()
        Gateway(w).deliver(claimed)
    calls=w.analytics.projection.calls
    with w.database.transaction() as session:
        row=session.get(OutboxMessage,message_id(w.account,operation))
        row.attempts=20;row.lease_token=uuid4()
        row.available_at=datetime.now(timezone.utc)-timedelta(seconds=1)
    assert sender(w).once()
    assert w.analytics.projection.calls==calls
    assert w.api.receipt(w.account,operation)['analyticsStatus']=='REVIEW'
    assert not sender(w).once()
    with w.database.transaction() as session:
        row=session.get(OutboxMessage,message_id(w.account,operation))
        assert row.attempts==20 and row.lease_token is None and row.failed_at is not None
        assert row.last_error=='DeliveryUnconfirmed'
    assert sender(w).requeue(w.account,operation)
    send(w)
    assert w.analytics.projection.calls==1
    assert w.api.receipt(w.account,operation)['analyticsStatus']=='COMPLETED'
    assert w.wallet.balance(w.account)['balance']==475 and weapon_count(w)==1


def test_same_operation_uuid_for_different_players_has_independent_sources(analytics_world):
    w=analytics_world;operation=uuid4();accounts=[w.account,uuid4()]
    for account in accounts:w.api.purchase(account,operation,'sword','v1')
    drain(w);send(w)
    assert purchase_id(accounts[0],operation)!=purchase_id(accounts[1],operation)
    assert len(w.analytics.projection.tables['fact_shop_purchases'])==2
    assert all(w.api.receipt(account,operation)['analyticsStatus']=='COMPLETED' for account in accounts)


def test_parallel_delivery_and_stale_lease_cannot_confirm_or_process_twice(analytics_world):
    w=analytics_world;operation,_=buy(w);drain(w)
    with w.uow() as work:first=work.repository.claim_analytics()
    receipt=Gateway(w).deliver(first);ready(w)
    with w.uow() as work:
        second=work.repository.claim_analytics()
        assert not work.repository.confirm_analytics(first['id'],first['lease'],receipt)
    assert w.api.receipt(w.account,operation)['analyticsStatus']=='PENDING'
    ready(w)
    with ThreadPoolExecutor(max_workers=4) as pool:list(pool.map(lambda _:sender(w).once(),range(4)))
    assert w.api.receipt(w.account,operation)['analyticsStatus']=='COMPLETED' and w.analytics.projection.calls==1


@pytest.mark.parametrize('failure',['funds','grant','conflict'])
def test_rejected_refunded_and_review_purchases_emit_no_success_event(analytics_world,failure):
    w=analytics_world
    if failure=='funds':w.catalog.price=1000
    if failure=='grant':w.definitions.ids=['sword.common']
    if failure=='conflict':w.gateway.conflict='grant'
    operation,_=buy(w);drain(w)
    receipt=w.api.receipt(w.account,operation)
    assert receipt['status'] in ('Rejected','Review') and receipt['analyticsStatus']=='SKIPPED'
    with w.database.transaction() as session:assert session.get(OutboxMessage,message_id(w.account,operation)) is None


def test_worker_gives_both_queues_a_turn_and_hides_exception_details(monkeypatch,caplog):
    from vatican_shop import worker
    from vatican_shop.application.use_cases import SettlePurchases
    calls=[]
    class Purchases:
        def once(self):calls.append('purchase');raise RuntimeError('secret SQL token')
    class Analytics:
        def once(self):calls.append('analytics');return False
    class Container:
        def get(self,type_):return Purchases() if type_ is SettlePurchases else Analytics()
        def close(self):calls.append('closed')
    monkeypatch.setattr(worker.Settings,'from_environment',lambda:None)
    monkeypatch.setattr(worker,'create_container',lambda settings:Container())
    monkeypatch.setattr('sys.argv',['worker','--once'])
    with pytest.raises(SystemExit):worker.main()
    assert calls==['purchase','analytics','closed'] and 'secret SQL token' not in caplog.text


def test_http_analytics_outage_pending_projection_and_recovery_preserve_owned_receipt(analytics_world,analytics_storage,monkeypatch):
    import httpx
    from types import SimpleNamespace
    from dishka import Provider, Scope, provide
    from fastapi.testclient import TestClient
    from vatican_analytics.application.use_cases import AnalyticsUseCases
    from vatican_analytics.bootstrap.di import create_container as analytics_container
    from vatican_analytics.bootstrap.settings import Settings as AnalyticsSettings
    from vatican_analytics.presentation import app as analytics_app
    from vatican_shop.application.ports import AnalyticsGateway, DefinitionsGateway
    from vatican_shop.bootstrap.settings import Settings
    from vatican_shop.integrations.analytics import HttpAnalyticsGateway
    from vatican_shop.presentation.app import create_app
    w=analytics_world;secret='s'*32;outsider=uuid4()
    class AnalyticsOverrides(Provider):
        @provide(scope=Scope.APP,provides=AnalyticsUseCases)
        def use_case(self):return w.analytics.api
    monkeypatch.setattr(analytics_app,'create_container',lambda settings:analytics_container(settings,AnalyticsOverrides()))
    settings=AnalyticsSettings(analytics_storage[1],secret,'clickhouse+http://localhost:1/unused')
    with TestClient(analytics_app.create_app(settings)) as analytics:
        online=False
        def transport(request):
            assert getattr(w.active,'depth',0)==0
            if not online:raise httpx.ConnectError('Offline analytics',request=request)
            response=analytics.post(request.url.path,content=request.content,headers=dict(request.headers))
            return httpx.Response(response.status_code,json=response.json())
        with httpx.Client(transport=httpx.MockTransport(transport)) as client:
            adapter=HttpAnalyticsGateway(client,'http://analytics',secret)
            class ShopOverrides(Provider):
                @provide(scope=Scope.APP,provides=AnalyticsGateway)
                def gateway(self):return adapter
                @provide(scope=Scope.APP,provides=DefinitionsGateway)
                def catalog(self):return w.catalog
            class Auth:
                def authenticate(self,token):return SimpleNamespace(account_id=w.account if token=='player' else outsider)
            app=create_app(Settings(str(w.database.engine.url.render_as_string(hide_password=False)),secret),
                           providers=(ShopOverrides(),),auth_gateway=Auth())
            with TestClient(app) as shop:
                headers={'Authorization':'Bearer player'};operation=uuid4()
                body={'operationId':str(operation),'offerId':'sword','catalogVersion':'v1'}
                path='/v1/purchases/'+str(operation)
                assert shop.post('/v1/purchases',headers=headers,json=body).status_code==202
                drain(w)
                delivery=app.state.dishka_container.get(DeliverAnalytics)
                assert delivery.once()
                assert shop.get(path,headers=headers).json()['analyticsStatus']=='PENDING'
                online=True;ready(w);assert delivery.once()
                assert w.analytics.api.get(purchase_id(w.account,operation),PURCHASE_EVENT)['status']=='PENDING'
                assert shop.get(path,headers=headers).json()['analyticsStatus']=='PENDING'
                assert w.analytics.worker.once()
                ready(w);assert delivery.once()
                final=shop.get(path,headers=headers).json()
                assert final['status']=='Completed' and final['analyticsStatus']=='COMPLETED' and 'plan' not in final
                assert shop.get('/v1/purchases',headers=headers).json()['purchases'][0]['analytics']==final['analytics']
                assert shop.post('/v1/purchases',headers=headers,json=body).json()['replayed']
                assert shop.get(path).status_code==401
                assert shop.get(path,headers={'Authorization':'Bearer outsider'}).status_code==404
                assert w.wallet.balance(w.account)['balance']==475 and weapon_count(w)==1
