from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager
from datetime import datetime, timedelta, timezone
import json
from threading import local
from types import SimpleNamespace
from uuid import UUID, uuid4

from dishka import Provider, Scope, provide
from fastapi.testclient import TestClient
import httpx
import pytest
from sqlalchemy import func, select, text
from sqlalchemy.exc import IntegrityError, DBAPIError
import psycopg

from vatican_shop.application.errors import ApplicationError
from vatican_shop.application.models import Principal
from vatican_shop.application.mock_payments import MockPayments, VerifyMockPayments, FulfillMockPayments
from vatican_shop.application.mock_ports import RewardBundlesGateway, MockProviderGateway, MockEntitlementGateway
from vatican_shop.bootstrap.settings import Settings
from vatican_shop.domain.mock_payments import MockPolicy, canonical, transaction_id
from vatican_shop.integrations.mock_payments import LocalMockProvider, MockEntitlementsHttpGateway
from vatican_shop.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory
from vatican_shop.persistence.postgres.tables import (PaymentOrder, PurchaseTransaction, EntitlementGrant,
    ProviderNotification, MockOrderState, MockFulfillmentJob, MockProviderPayment, MockProduct, IdempotencyRecord)
from vatican_shop.persistence.postgres.repositories.mock_payments import MockPaymentsRepository
from vatican_shop.presentation.app import create_app
from vatican_inventory.application.mock_payments import MockPaymentGrants
from vatican_inventory.application.errors import ApplicationError as InventoryError
from vatican_inventory.application.use_cases import InventoryUseCases
from vatican_inventory.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as InventoryUow
from vatican_inventory.persistence.postgres.tables import OwnedWeapon, MockPaymentReceipt
from vatican_inventory.bootstrap.settings import Settings as InventorySettings
from vatican_inventory.application.ports import DefinitionsGateway
from vatican_inventory.presentation.app import create_app as inventory_app
from vatican_backend.application.level_rewards import PublishRewardBundle, LevelRewardsUseCases
from vatican_backend.application.energy import EnergyService
from vatican_backend.domain.level_rewards import RewardPlan
from vatican_backend.persistence.postgres.tables import WeaponConfiguration
from vatican_backend.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as GameUow


@pytest.fixture
def mock_world(shop_storage, inventory_storage, service_storage_factory):
    db,url=shop_storage;inv,inv_url=inventory_storage;game,game_url=service_storage_factory('game_backend')
    active=local()
    for name,database in (('shop',db),('inventory',inv)):
        original=database.transaction
        @contextmanager
        def tracked(_name=name,_original=original):
            setattr(active,_name,getattr(active,_name,0)+1)
            try:
                with _original() as session:yield session
            finally:setattr(active,_name,getattr(active,_name)-1)
        database.transaction=tracked
    with game.transaction() as session:
        for key in ('sword.common','sword.rare'):
            session.add(WeaponConfiguration(configuration_id=key,configuration_json={'Id':key,'ExperiencePerLevel':100,'MaximumLevel':10}))
    class Auth:
        user,other,actor=uuid4(),uuid4(),uuid4()
        revoked=False
        def authenticate(self,token):
            assert getattr(active,'shop',0)==0
            if token=='outage':raise ApplicationError(503,'Auth unavailable')
            if token=='expired':raise ApplicationError(401,'Credential expired')
            return Principal(self.actor if token in ('admin','guest') else self.other if token=='other' else self.user,
                token=='guest',('ADMIN',) if token in ('admin','guest') and not self.revoked else ())
    class Definitions:
        down=False;known=True
        def get(self):
            assert getattr(active,'inventory',0)==0
            if self.down:raise InventoryError(503,'Private definitions outage')
            return [{'Id':key,'ExperiencePerLevel':100,'MaximumLevel':10}
                    for key in (('sword.common','sword.rare') if self.known else ('sword.common',))]
    auth,definitions=Auth(),Definitions()
    policy=MockPolicy(True,'test','c'*32,'w'*32,'r'*32,'i'*32)
    uow=PostgresUnitOfWorkFactory(db);iuow=InventoryUow(inv);guow=GameUow(game)
    game_rewards=LevelRewardsUseCases(guow,EnergyService('unused'))
    class Bundles:
        down=False
        def get(self,identity):
            assert getattr(active,'shop',0)==0
            if self.down:raise ApplicationError(503,'Bundles unavailable')
            return game_rewards.bundle(identity)
    bundles=Bundles();grants=MockPaymentGrants(iuow,definitions,True,'test',policy.grant_secret)
    real_provider=LocalMockProvider(uow,policy)
    class ProviderGateway:
        down=False
        def lookup(self,identity):
            assert getattr(active,'shop',0)==0
            if self.down:raise ApplicationError(503,'Private provider outage')
            return real_provider.lookup(identity)
    class Delivery:
        down=False;lost=False;calls=0
        def grant(self,job):
            assert getattr(active,'shop',0)==0
            self.calls+=1
            if self.down:raise ApplicationError(503,'Private inventory outage')
            from vatican_shop.domain.mock_payments import grant_payload
            try:result=grants.grant(grant_payload(job),policy.grant_secret)
            except InventoryError as error:raise ApplicationError(error.status_code,'Entitlement failed') from None
            if self.lost:
                self.lost=False
                raise ApplicationError(503,'Private grant acknowledgement lost')
            return result
    provider,delivery=ProviderGateway(),Delivery()
    api=MockPayments(uow,auth,bundles,policy)
    w=SimpleNamespace(db=db,url=url,inventory=inv,inv_url=inv_url,game=game,game_url=game_url,auth=auth,policy=policy,
        uow=uow,iuow=iuow,guow=guow,definitions=definitions,bundles=bundles,grants=grants,provider=provider,delivery=delivery,
        api=api,verify=VerifyMockPayments(uow,provider,policy),fulfill=FulfillMockPayments(uow,delivery,policy))
    def publish(quantity=1,version=1,sku='test.sword',price=199):
        identity=uuid4()
        plan=RewardPlan.parse({'schemaVersion':1,'bundleId':str(identity),'items':[
            {'itemIndex':0,'rewardType':'WEAPON','rewardRef':'sword.rare','quantity':quantity}]})
        PublishRewardBundle(guow).execute('mock_'+identity.hex,1,plan)
        content={'sku':sku,'version':version,'externalSku':sku+'.v'+str(version),'rewardBundleId':identity,'priceMinor':price,'currencyIso':'USD'}
        return api.publish('admin',policy.control_secret,content),content
    w.publish=publish
    return w


def order(w,product=None,key=None):
    if product is None:product,_=w.publish()
    result=w.api.create('user',UUID(product['productId']),key or uuid4())
    return UUID(result['orderId']),result


def simulate(w,identity,scenario='SUCCESS',unavailable=False,key=None):
    return w.api.simulate('admin',w.policy.control_secret,identity,key or uuid4(),scenario,unavailable)


def hook(w,signal):
    return w.api.webhook(canonical(signal['webhook']['body']),signal['webhook']['signature'])


def drain(w):
    for _ in range(100):
        a=w.verify.once();b=w.fulfill.once()
        if not a and not b:return
    pytest.fail('MOCK queues did not quiesce')


def due(w,identity,verify=False):
    with w.db.transaction() as session:
        if verify:session.get(MockOrderState,identity).available_at=datetime.now(timezone.utc)-timedelta(seconds=1)
        for job in session.scalars(select(MockFulfillmentJob).where(MockFulfillmentJob.order_id==identity)):
            job.available_at=datetime.now(timezone.utc)-timedelta(seconds=1)


def count(database,table):
    with database.transaction() as session:return session.scalar(select(func.count()).select_from(table))


def assert_paid(w,identity,quantity=1):
    result=w.api.get('user',identity)
    assert result['status']=='FULFILLED' and result['fulfillmentStatus']=='COMPLETED'
    with w.inventory.transaction() as session:
        owned=list(session.scalars(select(OwnedWeapon).where(OwnedWeapon.configuration_id=='sword.rare')))
        assert len(owned)==quantity
    assert count(w.inventory,MockPaymentReceipt)==quantity
    assert count(w.db,PurchaseTransaction)==1 and count(w.db,EntitlementGrant)==1
    assert len({unit['receipt']['instanceId'] for unit in result['units']})==quantity
    return result


def test_success_uses_frozen_price_and_published_reward_with_real_inventory(mock_world):
    w=mock_world;product,content=w.publish(quantity=2);key=uuid4();identity,first=order(w,product,key)
    signal=simulate(w,identity);hook(w,signal)
    w.api.availability('admin',w.policy.control_secret,UUID(product['productId']),False,uuid4())
    drain(w);result=assert_paid(w,identity,2)
    assert result['product']['priceMinor']==199 and result['transactionId']==str(transaction_id(identity))
    replay=w.api.create('user',UUID(product['productId']),key)
    assert replay['replayed'] and replay['status']=='FULFILLED'


def test_twenty_duplicate_webhooks_and_sdk_confirmations_have_one_effect(mock_world):
    w=mock_world;identity,_=order(w);signal=simulate(w,identity)
    assert sum(not hook(w,signal)['replayed'] for _ in range(20))==1
    drain(w);before=assert_paid(w,identity)
    for _ in range(20):w.api.confirm('user',identity,signal['receipt'])
    drain(w);after=assert_paid(w,identity)
    assert after['units']==before['units'] and count(w.db,ProviderNotification)==2


def test_parallel_checkout_has_one_order_and_conflicting_reuse_is_rejected(mock_world):
    w=mock_world;product,_=w.publish();key=uuid4()
    with ThreadPoolExecutor(max_workers=8) as pool:
        replies=list(pool.map(lambda _:w.api.create('user',UUID(product['productId']),key),range(32)))
    assert len({r['orderId'] for r in replies})==1 and sum(not r['replayed'] for r in replies)==1
    assert count(w.db,PaymentOrder)==1 and count(w.db,MockProviderPayment)==1
    other,_=w.publish(sku='test.other')
    with pytest.raises(ApplicationError) as error:w.api.create('user',UUID(other['productId']),key)
    assert error.value.status_code==409


def test_parallel_webhooks_workers_and_simulation_retries_are_idempotent(mock_world):
    w=mock_world;identity,_=order(w);key=uuid4()
    with ThreadPoolExecutor(max_workers=8) as pool:
        events=list(pool.map(lambda _:simulate(w,identity,key=key),range(20)))
    assert sum(not e['replayed'] for e in events)==1
    with ThreadPoolExecutor(max_workers=8) as pool:list(pool.map(lambda e:hook(w,e),events))
    with ThreadPoolExecutor(max_workers=6) as pool:list(pool.map(lambda _:drain(w),range(6)))
    drain(w);assert_paid(w,identity)


def test_two_consumable_orders_with_same_sku_and_key_for_different_owners_are_independent(mock_world):
    w=mock_world;product,_=w.publish();key=uuid4()
    a,_=order(w,product,key);b=UUID(w.api.create('other',UUID(product['productId']),key)['orderId'])
    assert a!=b
    for identity in (a,b):hook(w,simulate(w,identity))
    drain(w)
    assert count(w.db,PurchaseTransaction)==2 and count(w.db,EntitlementGrant)==2
    assert count(w.inventory,MockPaymentReceipt)==2
    with pytest.raises(ApplicationError) as error:w.api.get('other',a)
    assert error.value.status_code==404


def test_same_owner_can_buy_a_consumable_again_with_a_new_order_key(mock_world):
    w=mock_world;product,_=w.publish();ids=[order(w,product)[0] for _ in range(2)]
    for identity in ids:simulate(w,identity)
    drain(w)
    assert all(w.api.get('user',identity)['status']=='FULFILLED' for identity in ids)
    assert count(w.inventory,MockPaymentReceipt)==2 and count(w.db,PurchaseTransaction)==2


@pytest.mark.parametrize('scenario',['PENDING','DECLINED'])
def test_unpaid_and_declined_payments_never_issue_rewards(mock_world,scenario):
    w=mock_world;identity,_=order(w);signal=simulate(w,identity,scenario);hook(w,signal);drain(w)
    assert count(w.inventory,MockPaymentReceipt)==count(w.db,PurchaseTransaction)==0
    assert w.api.get('user',identity)['status']==scenario


def test_lost_sdk_response_and_lost_webhook_reconcile_from_provider_truth(mock_world):
    w=mock_world;identity,_=order(w);signal=simulate(w,identity,'TIMEOUT')
    assert signal['receipt'] is None and signal['sdkStatus']=='TIMEOUT'
    drain(w);assert_paid(w,identity)
    # A delayed webhook after completion does not recreate jobs.
    hook(w,signal);drain(w);assert_paid(w,identity)


def test_provider_outage_never_treats_signed_webhook_as_payment_proof(mock_world):
    w=mock_world;identity,_=order(w);hook(w,simulate(w,identity,unavailable=True));drain(w)
    assert w.api.get('user',identity)['status']=='UNKNOWN'
    assert count(w.db,PurchaseTransaction)==count(w.inventory,MockPaymentReceipt)==0
    hook(w,simulate(w,identity));drain(w);assert_paid(w,identity)


def test_lost_inventory_ack_and_content_outage_replay_one_source(mock_world):
    w=mock_world;identity,_=order(w);simulate(w,identity);w.delivery.lost=True
    drain(w)
    assert count(w.inventory,MockPaymentReceipt)==1 and w.api.get('user',identity)['status']=='VERIFIED'
    w.definitions.down=True;due(w,identity);drain(w);assert_paid(w,identity)


def test_refund_before_verification_cancels_without_issuing_or_fabricating_revocation(mock_world):
    w=mock_world;identity,_=order(w);success=simulate(w,identity);refund=simulate(w,identity,'REFUND')
    hook(w,refund);hook(w,success);drain(w)
    result=w.api.get('user',identity)
    assert result['status']=='REFUNDED' and result['fulfillmentStatus']=='CANCELLED'
    assert count(w.inventory,MockPaymentReceipt)==count(w.db,EntitlementGrant)==0
    assert count(w.db,PurchaseTransaction)==1


def test_refund_of_verified_but_not_dispatched_order_cancels_pending_jobs(mock_world):
    w=mock_world;identity,_=order(w);simulate(w,identity);assert w.verify.once()
    hook(w,simulate(w,identity,'REFUND'));assert w.verify.once()
    assert not w.fulfill.once()
    assert w.api.get('user',identity)['fulfillmentStatus']=='CANCELLED'
    assert count(w.inventory,MockPaymentReceipt)==0


def test_refund_after_delivery_requires_review_and_cannot_reopen_grant(mock_world):
    w=mock_world;identity,_=order(w);old=simulate(w,identity);drain(w);assert_paid(w,identity)
    hook(w,simulate(w,identity,'REFUND'));drain(w);hook(w,old);drain(w)
    result=w.api.get('user',identity)
    assert result['status']=='REFUNDED' and result['fulfillmentStatus']=='REVIEW'
    assert result['reviewCode']=='RefundAfterDispatch' and count(w.inventory,MockPaymentReceipt)==1
    with pytest.raises(ApplicationError):w.api.retry('admin',w.policy.control_secret,identity,result['version'],uuid4(),'Do not regrant refund')
    with w.db.transaction() as session:assert session.scalar(select(EntitlementGrant.revoked_at)) is None


def test_inventory_exhaustion_then_admin_retry_keeps_completed_units_and_receipts(mock_world):
    w=mock_world;product,_=w.publish(quantity=2);identity,_=order(w,product);simulate(w,identity)
    assert w.verify.once() and w.fulfill.once()
    with w.db.transaction() as session:
        job=session.get(MockFulfillmentJob,(identity,0,1));job.attempts=19
    w.delivery.down=True;assert w.fulfill.once()
    result=w.api.get('user',identity);assert result['fulfillmentStatus']=='REVIEW'
    assert count(w.inventory,MockPaymentReceipt)==1
    w.delivery.down=False;key=uuid4()
    first=w.api.retry('admin',w.policy.control_secret,identity,result['version'],key,'Inventory restored')
    drain(w);assert_paid(w,identity,2)
    assert w.api.retry('admin',w.policy.control_secret,identity,result['version'],key,'Inventory restored')['replayed']


def test_stale_delivery_lease_cannot_overwrite_replacement(mock_world):
    w=mock_world;identity,_=order(w);simulate(w,identity);w.verify.once()
    with w.uow() as work:old=work.mock.claim_fulfillment()
    receipt=w.delivery.grant(old);due(w,identity)
    with w.uow() as work:new=work.mock.claim_fulfillment()
    with w.uow() as work:assert not work.mock.finish_fulfillment(old,receipt)
    replacement_receipt=w.delivery.grant(new)
    with w.uow() as work:assert work.mock.finish_fulfillment(new,replacement_receipt)
    assert_paid(w,identity)


def test_webhook_arrival_invalidates_stale_verifier_lease(mock_world):
    w=mock_world;identity,_=order(w);event=simulate(w,identity)
    with w.uow() as work:old=work.mock.claim_verification()
    truth=w.policy.parse_receipt(w.provider.lookup(identity))
    hook(w,event)
    with w.uow() as work:assert not work.mock.apply_verification(old,truth)
    drain(w);assert_paid(w,identity)


def test_lost_grant_commit_does_not_duplicate_inventory_after_restart(mock_world,monkeypatch):
    w=mock_world;identity,_=order(w);simulate(w,identity);assert w.verify.once()
    with monkeypatch.context() as patch:
        def fail(*args,**kw):raise RuntimeError('Private commit failure after inventory ACK')
        patch.setattr(MockPaymentsRepository,'finish_fulfillment',fail)
        with pytest.raises(RuntimeError):w.fulfill.once()
    assert count(w.inventory,MockPaymentReceipt)==1
    due(w,identity);w.fulfill=FulfillMockPayments(w.uow,w.delivery,w.policy);drain(w);assert_paid(w,identity)


def test_receipt_owner_and_provider_details_cannot_be_rebound(mock_world):
    w=mock_world;a,_=order(w);receipt=simulate(w,a)['receipt']
    with pytest.raises(ApplicationError) as error:w.api.confirm('other',a,receipt)
    assert error.value.status_code==404
    product,_=w.publish(sku='another.product');b,_=order(w,product)
    for patch in ({'orderId':str(b)},{'accountId':str(w.auth.other)},{'externalSku':'wrong'},{'priceMinor':0},{'environment':'PRODUCTION'}):
        truth={**w.policy.parse_receipt(receipt),**patch}
        with pytest.raises(ApplicationError) as error:w.api.confirm('user',a,w.policy.receipt(truth))
        assert error.value.status_code==422
    assert count(w.db,PurchaseTransaction)==0


def test_invalid_signatures_and_changed_event_payload_do_not_persist(mock_world):
    w=mock_world;identity,_=order(w);signal=simulate(w,identity);body=canonical(signal['webhook']['body'])
    for signature in (None,'','a'*64,'é'*64):
        with pytest.raises(ApplicationError) as error:w.api.webhook(body,signature)
        assert error.value.status_code==401
    assert count(w.db,ProviderNotification)==0
    changed={**signal['webhook']['body'],'environment':'PRODUCTION'};raw=canonical(changed)
    with pytest.raises(ApplicationError) as error:w.api.webhook(raw,w.policy.sign_webhook(raw))
    assert error.value.status_code==422
    assert count(w.db,ProviderNotification)==0


@pytest.mark.parametrize('token,credential,code',[('user','c'*32,403),('guest','c'*32,403),('admin','wrong',401),('expired','c'*32,401),('outage','c'*32,503)])
def test_simulator_requires_fresh_admin_and_separate_control_credential(mock_world,token,credential,code):
    w=mock_world;identity,_=order(w)
    with pytest.raises(ApplicationError) as error:w.api.simulate(token,credential,identity,uuid4(),'SUCCESS')
    assert error.value.status_code==code
    assert count(w.db,PurchaseTransaction)==0


def test_revoked_admin_cannot_replay_a_previously_successful_simulation(mock_world):
    w=mock_world;identity,_=order(w);key=uuid4();simulate(w,identity,key=key);w.auth.revoked=True
    with pytest.raises(ApplicationError) as error:simulate(w,identity,key=key)
    assert error.value.status_code==403


def test_inventory_receipt_namespace_is_global_and_separate_from_prestige_purchase(mock_world):
    w=mock_world;identity,_=order(w);simulate(w,identity);w.verify.once()
    with w.uow() as work:job=work.mock.claim_fulfillment()
    receipt=w.delivery.grant(job)
    from vatican_shop.domain.mock_payments import grant_payload
    with pytest.raises(InventoryError) as error:w.grants.grant({**grant_payload(job),'accountId':str(w.auth.other)},w.policy.grant_secret)
    assert error.value.status_code==409
    ordinary=InventoryUseCases(w.iuow,w.definitions).grant(w.auth.user,job['transactionId'],'sword.rare')
    assert ordinary['instanceId']!=receipt['instanceId']


def test_http_mock_flow_uses_real_inventory_endpoint_and_forbids_client_price(mock_world):
    w=mock_world
    class Defs(Provider):
        @provide(scope=Scope.APP,provides=DefinitionsGateway)
        def definitions(self):return w.definitions
    inv_settings=InventorySettings(w.inv_url,'s'*32,app_environment='test',mock_payments_enabled=True,mock_grant_secret=w.policy.grant_secret)
    with TestClient(inventory_app(inv_settings,providers=(Defs(),),auth_gateway=w.auth)) as inv:
        def handler(request):
            response=inv.post(request.url.path,content=request.content,headers=dict(request.headers))
            return httpx.Response(response.status_code,json=response.json())
        settings=Settings(w.url,'s'*32,app_environment='test',mock_payments_enabled=True,mock_control_secret=w.policy.control_secret,
            mock_webhook_secret=w.policy.webhook_secret,mock_receipt_secret=w.policy.receipt_secret,mock_grant_secret=w.policy.grant_secret)
        with httpx.Client(transport=httpx.MockTransport(handler)) as transport:
            gateway=MockEntitlementsHttpGateway(transport,settings)
            class Overrides(Provider):
                @provide(scope=Scope.APP,provides=RewardBundlesGateway)
                def bundles(self):return w.bundles
                @provide(scope=Scope.APP,provides=MockEntitlementGateway)
                def grants(self):return gateway
            with TestClient(create_app(settings,providers=(Overrides(),),auth_gateway=w.auth)) as client:
                product,_=w.publish();headers={'Authorization':'Bearer user'}
                body={'productId':product['productId'],'requestKey':str(uuid4())}
                assert client.post('/v1/mock/orders',json=body).status_code==401
                assert client.post('/v1/mock/orders',json={**body,'priceMinor':0},headers=headers).status_code==422
                created=client.post('/v1/mock/orders',json=body,headers=headers);assert created.status_code==202
                identity=UUID(created.json()['orderId']);signal=simulate(w,identity)
                raw=canonical(signal['webhook']['body'])
                assert client.post('/v1/mock/provider/webhook',content=raw,headers={'X-Mock-Signature':signal['webhook']['signature']}).status_code==200
                verifier=client.app.state.dishka_container.get(VerifyMockPayments)
                fulfill=client.app.state.dishka_container.get(FulfillMockPayments)
                assert verifier.once() and fulfill.once()
                assert client.get('/v1/mock/orders/'+str(identity),headers=headers).json()['status']=='FULFILLED'
                assert client.get('/v1/mock/orders/'+str(identity),headers={'Authorization':'Bearer other'}).status_code==404


def test_mock_routes_are_absent_when_disabled_and_production_config_is_rejected(mock_world):
    w=mock_world
    with TestClient(create_app(Settings(w.url,'s'*32),auth_gateway=w.auth)) as client:
        assert client.get('/v1/mock/products').status_code==404
        assert not any('/mock/' in path for path in client.get('/openapi.json').json()['paths'])
    with pytest.raises(ValueError):Settings(w.url,'s'*32,mock_payments_enabled=True)
    with pytest.raises(ValueError):InventorySettings(w.inv_url,'s'*32,mock_payments_enabled=True,mock_grant_secret='i'*32)
    assert 'c'*32 not in repr(w.policy)
