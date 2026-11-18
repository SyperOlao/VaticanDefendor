"""Permanent upgrades: atomic purchase intent, durable wallet debit and pinned bonuses."""
from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager
from threading import local
from dataclasses import replace
from datetime import datetime,timedelta,timezone
from decimal import Decimal
import json
from pathlib import Path
from uuid import UUID,uuid4
import pytest
from sqlalchemy import select,func,text
from sqlalchemy.exc import IntegrityError
from vatican_backend.application.prestige import PublishPrestige
from vatican_backend.application.messaging import DeliverRewards
from vatican_backend.application.errors import ApplicationError
from vatican_backend.application.commands import ServerRegistration,Assignment,RunResult
from vatican_backend.domain.prestige import PrestigeRules,PAYMENT_EVENT
from vatican_backend.persistence.postgres.tables import (PlayerPrestigeUpgrade,PrestigePurchase,PrestigeConfiguration,
    OutboxMessage,Profile,Run,RunEnergyCharge,PrestigeUpgradeDefinition)
from vatican_wallet.application.use_cases import WalletUseCases
from vatican_wallet.application.errors import ApplicationError as WalletError
from vatican_wallet.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as WalletUow
from vatican_wallet.persistence.postgres.tables import PrestigePaymentReceipt,EconomyTransaction,ConsumedMessage

from .test_local_runs import local_game

AUTH={'Authorization':'Bearer test-token'}
CODE='hero.attack_damage'


@pytest.fixture
def prestige_game(backend,wallet_storage):
    client,settings,slot=backend
    service=client.app.state.use_cases.prestige
    account=UUID(client.app.state.test_player['accountId'])
    service.settings=replace(settings,prestige_ruleset_id='prestige-v1')
    rules=PrestigeRules(json.loads((Path(__file__).parents[1]/'prestige-example.json').read_text()))
    PublishPrestige(service.uow).execute(rules)
    active=local();database=client.app.state.database;transaction=database.transaction
    @contextmanager
    def tracked_transaction():
        active.game=getattr(active,'game',0)+1
        try:
            with transaction() as session:yield session
        finally:active.game-=1
    database.transaction=tracked_transaction
    wallet=WalletUseCases(WalletUow(wallet_storage[0]))
    return {'client':client,'service':service,'wallet':wallet,'wallet_db':wallet_storage[0],
        'account':account,'db':client.app.state.database,'active':active,'slot':slot,'rules':rules}


def buy(w,key=None,code=CODE,level=0,ruleset='prestige-v1'):
    return w['service'].buy(w['account'],key or uuid4(),ruleset,code,level)


def state(w,reply):return w['service'].get(w['account'],UUID(reply['purchaseId']))


def current(w,code=CODE):
    with w['db'].transaction() as session:
        row=session.get(PlayerPrestigeUpgrade,(w['account'],code))
        return row.level if row else 0


class Gateway:
    def __init__(self,w,down=False,lost=False,wrong=False):self.w,self.down,self.lost,self.wrong=w,down,lost,wrong
    def deliver(self,message):
        assert getattr(self.w['active'],'game',0)==0
        if self.down:raise ApplicationError(503,'Wallet unavailable')
        try:reply=self.w['wallet'].receive(message['id'],message['event_name'],message['schema_version'],message['payload'])
        except WalletError as error:raise ApplicationError(error.status_code,'Wallet refused') from None
        if self.lost:raise ApplicationError(503,'Lost wallet confirmation')
        receipt=reply['receipt']
        return {**receipt,'amount':receipt['amount']+1} if self.wrong else receipt


def drain(w,**options):
    worker=DeliverRewards(w['service'].uow,Gateway(w,**options),prestige=w['service'])
    for _ in range(10):
        if not worker.once():return worker
    raise AssertionError('Did not quiesce')


def ready(w):
    with w['db'].transaction() as session:
        for row in session.scalars(select(OutboxMessage).where(OutboxMessage.event_name==PAYMENT_EVENT,OutboxMessage.published_at.is_(None))):
            row.available_at=datetime.now(timezone.utc)-timedelta(seconds=1)


def test_purchase_applies_after_payment_and_returns_stable_receipt(prestige_game):
    w=prestige_game;key=uuid4();reply=buy(w,key)
    assert reply['status']=='PENDING' and reply['cost']==100 and current(w)==0
    assert w['service'].catalog(w['account'])['bonuses']['modifiers']==[]
    drain(w)
    complete=state(w,reply)
    assert complete['status']=='COMPLETED' and complete['fromLevel']==0 and complete['toLevel']==1
    assert complete['result']['bonus']['modifierValue']=='0.050000' and complete['result']['profileVersion']==2
    assert complete['paymentReceipt']['balanceAfter']==w['wallet'].balance(w['account'])['balance']==400
    assert current(w)==1
    assert w['service'].catalog(w['account'])['bonuses']['modifiers']==[{'target':'HERO','stat':'ATTACK_DAMAGE','multiplier':'1.050000'}]
    assert buy(w,key)['replayed'] and buy(w,key)['result']==complete['result']
    with w['db'].transaction() as session:
        assert session.scalar(select(func.count()).select_from(PrestigePurchase))==1
        assert session.scalar(select(func.count()).select_from(OutboxMessage))==1
        assert session.get(Profile,w['account']).version==2
    with w['wallet_db'].transaction() as session:
        assert session.scalar(select(func.count()).select_from(PrestigePaymentReceipt))==1
        assert session.scalar(select(func.count()).select_from(EconomyTransaction).where(EconomyTransaction.source_type=='PRESTIGE_UPGRADE'))==1


def test_second_level_replaces_cumulative_bonus_and_stale_intent_cannot_buy_another(prestige_game):
    w=prestige_game;first=buy(w);drain(w)
    with pytest.raises(ApplicationError,match='level changed'):buy(w)
    second=buy(w,level=1);drain(w)
    assert state(w,second)['status']=='COMPLETED' and current(w)==2
    assert w['wallet'].balance(w['account'])['balance']==200
    assert w['service'].catalog(w['account'])['bonuses']['modifiers'][0]['multiplier']=='1.100000'
    assert state(w,first)['result']['bonus']['modifierValue']=='0.050000'


def test_concurrent_same_key_and_distinct_intents_produce_one_debit(prestige_game):
    w=prestige_game;key=uuid4()
    with ThreadPoolExecutor(max_workers=6) as pool:replies=list(pool.map(lambda _:buy(w,key),range(6)))
    assert len({r['purchaseId'] for r in replies})==1 and sum(not r['replayed'] for r in replies)==1
    with pytest.raises(ApplicationError,match='pending'):buy(w,code='wall.health')
    with ThreadPoolExecutor(max_workers=4) as pool:list(pool.map(lambda _:drain(w),range(4)))
    assert current(w)==1 and w['wallet'].balance(w['account'])['balance']==400


@pytest.mark.parametrize('patch',[{'code':'wall.health'},{'level':1},{'ruleset':'other'}])
def test_request_key_cannot_change_payload(prestige_game,patch):
    w=prestige_game;key=uuid4();buy(w,key)
    with pytest.raises(ApplicationError,match='payload changed'):buy(w,key,**patch)


@pytest.mark.parametrize('failure',['down','lost','wrong'])
def test_wallet_failure_and_lost_ack_resume_same_purchase_once(prestige_game,failure):
    w=prestige_game;reply=buy(w)
    drain(w,**{failure:True})
    assert state(w,reply)['status']=='PENDING' and current(w)==0
    if failure!='down':assert w['wallet'].balance(w['account'])['balance']==400
    ready(w);drain(w)
    assert state(w,reply)['status']=='COMPLETED' and current(w)==1
    assert w['wallet'].balance(w['account'])['balance']==400


def test_insufficient_funds_is_durable_and_new_intent_can_retry_after_top_up(prestige_game):
    w=prestige_game
    w['wallet'].change(w['account'],uuid4(),450)
    key=uuid4();reply=buy(w,key);drain(w)
    assert state(w,reply)['status']=='REJECTED' and current(w)==0
    w['wallet'].receive(uuid4(),'run.currency_reward',1,{'accountId':str(w['account']),'runId':str(uuid4()),'amount':200})
    assert buy(w,key)['status']=='REJECTED' and w['wallet'].balance(w['account'])['balance']==250
    retry=buy(w);drain(w)
    assert state(w,retry)['status']=='COMPLETED' and w['wallet'].balance(w['account'])['balance']==150


def test_local_apply_failure_rolls_back_bonus_and_confirmation_after_remote_charge(prestige_game,monkeypatch):
    from vatican_backend.persistence.postgres.repositories.profiles import ProfilesRepository
    w=prestige_game;reply=buy(w);original=ProfilesRepository.bump
    def fail(*args,**kwargs):raise RuntimeError('Profile commit interrupted')
    monkeypatch.setattr(ProfilesRepository,'bump',fail)
    with pytest.raises(RuntimeError):drain(w)
    assert w['wallet'].balance(w['account'])['balance']==400 and current(w)==0
    assert state(w,reply)['status']=='PENDING'
    monkeypatch.setattr(ProfilesRepository,'bump',original)
    ready(w);drain(w)
    assert current(w)==1 and state(w,reply)['status']=='COMPLETED'
    assert w['wallet'].balance(w['account'])['balance']==400


def test_game_db_confirmation_failure_and_worker_restart_preserve_one_bonus(prestige_game):
    w=prestige_game;reply=buy(w)
    with w['db'].engine.begin() as conn:conn.execute(text("ALTER TABLE prestige_purchases ADD CONSTRAINT injected_confirmation CHECK (status <> 'COMPLETED')"))
    try:
        with pytest.raises(IntegrityError):drain(w)
        assert current(w)==0 and w['wallet'].balance(w['account'])['balance']==400
    finally:
        with w['db'].engine.begin() as conn:conn.execute(text('ALTER TABLE prestige_purchases DROP CONSTRAINT injected_confirmation'))
    ready(w);drain(w)
    assert state(w,reply)['status']=='COMPLETED' and current(w)==1


def test_review_can_be_requeued_and_blocks_new_intents_until_resolved(prestige_game):
    w=prestige_game;reply=buy(w)
    with w['db'].transaction() as session:session.get(OutboxMessage,UUID(reply['paymentMessageId'])).attempts=19
    worker=drain(w,lost=True)
    assert state(w,reply)['status']=='REVIEW' and current(w)==0
    with pytest.raises(ApplicationError,match='pending'):buy(w,code='wall.health')
    assert worker.requeue(UUID(reply['paymentMessageId']))
    drain(w)
    assert current(w)==1 and w['wallet'].balance(w['account'])['balance']==400


def test_retired_configuration_keeps_frozen_pending_plan_and_request_replay(prestige_game):
    w=prestige_game;key=uuid4();reply=buy(w,key)
    w['service'].settings=replace(w['service'].settings,prestige_ruleset_id='another')
    with w['db'].transaction() as session:
        for row in session.scalars(select(PrestigeUpgradeDefinition)):row.modifier_value=Decimal('4');row.cost=999
    drain(w)
    complete=buy(w,key)
    assert complete['status']=='COMPLETED' and complete['cost']==100 and complete['result']['bonus']['modifierValue']=='0.050000'
    with pytest.raises(ApplicationError,match='catalog changed'):buy(w,level=1)


def test_wallet_source_and_alias_are_immutable_even_with_new_message_id(prestige_game):
    w=prestige_game;reply=buy(w);drain(w)
    with w['db'].transaction() as session:message=session.get(OutboxMessage,UUID(reply['paymentMessageId']))
    body=message.payload
    for _ in range(2):assert w['wallet'].receive(uuid4(),PAYMENT_EVENT,1,body)['receipt']['balanceAfter']==400
    for patch in ({'amount':101},{'code':'wall.health'},{'fromLevel':1,'toLevel':2},{'accountId':str(uuid4())},{'rulesetId':'other'}):
        with pytest.raises(WalletError) as error:w['wallet'].receive(uuid4(),PAYMENT_EVENT,1,{**body,**patch})
        assert error.value.status_code==409
    with pytest.raises(WalletError):w['wallet'].receive(message.id,PAYMENT_EVENT,1,{**body,'purchaseId':str(uuid4())})
    assert w['wallet'].balance(w['account'])['balance']==400


def test_http_contract_is_owner_scoped_and_rejects_client_price_or_bonus(prestige_game):
    w=prestige_game;client=w['client'];endpoint='/v1/prestige-upgrades'
    assert client.get(endpoint).status_code==401
    assert client.get(endpoint,headers=AUTH).json()['available']
    body={'requestKey':str(uuid4()),'rulesetId':'prestige-v1','code':CODE,'expectedLevel':0}
    for patch in ({'cost':1},{'modifierValue':'5'},{'accountId':str(uuid4())},{'expectedLevel':True},{'expectedLevel':'0'}):
        assert client.post(endpoint+'/purchases',headers=AUTH,json={**body,**patch}).status_code==422
    reply=client.post(endpoint+'/purchases',headers=AUTH,json=body)
    assert reply.status_code==202,reply.text
    purchase=reply.json()
    with pytest.raises(ApplicationError) as error:w['service'].get(uuid4(),UUID(purchase['purchaseId']))
    assert error.value.status_code==404
    drain(w)
    assert client.post(endpoint+'/purchases',headers=AUTH,json=body).status_code==200
    assert client.get(endpoint+'/purchases/'+purchase['purchaseId'],headers=AUTH).json()['status']=='COMPLETED'


def test_dedicated_bonuses_require_capability_and_are_pinned_for_next_run(prestige_game):
    w=prestige_game;buy(w);drain(w)
    runs=w['client'].app.state.use_cases.runs;slot=w['slot'];account=w['account']
    runs.register_server(slot.server_id,slot.secret,ServerRegistration('sha256-test-release',3,'udp-loopback'))
    with pytest.raises(ApplicationError,match='No live'):runs.assign(account,respond_async=True,request_key=uuid4())
    with w['db'].transaction() as session:assert session.scalar(select(func.count()).select_from(RunEnergyCharge))==0
    runs.register_server(slot.server_id,slot.secret,ServerRegistration('sha256-test-release',3,'udp-loopback',None,1))
    pending=runs.assign(account,respond_async=True,request_key=uuid4())
    initial=runs.server_assignment(slot.server_id,slot.secret)
    assert json.loads(initial['prestigeJson'])['modifiers'][0]['multiplier']=='1.050000'
    buy(w,level=1);drain(w)
    assert runs.server_assignment(slot.server_id,slot.secret)['prestigeJson']==initial['prestigeJson']
    with w['db'].transaction() as session:assert session.get(Run,UUID(pending['runId'])).prestige_snapshot['upgrades'][0]['level']==1


def test_publication_is_immutable_and_cannot_rename_or_retarget_a_bonus(prestige_game):
    w=prestige_game;publish=PublishPrestige(w['service'].uow)
    assert publish.execute(w['rules'])['replayed']
    value=w['rules'].snapshot();value['upgrades'][0]['levels'][0]['cost']=99
    with pytest.raises(ApplicationError,match='immutable'):publish.execute(PrestigeRules(value))
    value['rulesetId']='other';value['upgrades'][0]['code']='renamed'
    with pytest.raises(ApplicationError,match='reassigned'):publish.execute(PrestigeRules(value))


def test_purchase_creation_failure_does_not_leave_a_charge_message(prestige_game,monkeypatch):
    from vatican_backend.persistence.postgres.repositories.prestige import PrestigeRepository
    w=prestige_game;create=PrestigeRepository.create
    def fail(*args,**kwargs):create(*args,**kwargs);raise RuntimeError('Intent creation interrupted')
    monkeypatch.setattr(PrestigeRepository,'create',fail)
    with pytest.raises(RuntimeError):buy(w)
    with w['db'].transaction() as session:
        assert session.scalar(select(func.count()).select_from(PrestigePurchase))==session.scalar(select(func.count()).select_from(OutboxMessage))==0


def test_wallet_receipt_insert_failure_rolls_back_debit_and_retry_can_apply(prestige_game):
    w=prestige_game;reply=buy(w)
    with w['wallet_db'].engine.begin() as conn:
        conn.execute(text('ALTER TABLE prestige_payment_receipts ADD CONSTRAINT injected_wallet_receipt CHECK (false)'))
    try:
        with pytest.raises(IntegrityError):drain(w)
        assert w['wallet'].balance(w['account'])['balance']==500 and current(w)==0
    finally:
        with w['wallet_db'].engine.begin() as conn:conn.execute(text('ALTER TABLE prestige_payment_receipts DROP CONSTRAINT injected_wallet_receipt'))
    ready(w);drain(w)
    assert current(w)==1 and w['wallet'].balance(w['account'])['balance']==400


@pytest.mark.parametrize('spend,status',[(450,'REJECTED'),(0,'COMPLETED')])
def test_real_wallet_http_contract_acknowledges_payment_and_persists_refusal(prestige_game,wallet_storage,spend,status):
    from fastapi.testclient import TestClient
    from vatican_wallet.presentation.app import create_app
    from vatican_wallet.bootstrap.settings import Settings
    from vatican_backend.integrations.http import HttpRewardDelivery
    w=prestige_game;secret='unit-test-service-secret-with-enough-length'
    app=create_app(Settings(wallet_storage[1],secret))
    with TestClient(app) as client:
        if spend:w['wallet'].change(w['account'],uuid4(),spend)
        reply=buy(w)
        assert client.post('/internal/messages',json={}).status_code in (401,422)
        gateway=HttpRewardDelivery(client,{'wallet':''},secret)
        worker=DeliverRewards(w['service'].uow,gateway,prestige=w['service'])
        assert worker.once()
        rejected=state(w,reply)
        assert rejected['status']==status and rejected['paymentReceipt']['code']==CODE
        if status=='REJECTED':
            assert rejected['paymentReceipt']['rejectionCode']=='InsufficientFunds' and current(w)==0
        else:
            assert current(w)==1 and w['wallet'].balance(w['account'])['balance']==400


def test_dedicated_recovery_requires_capability_and_reuses_pinned_snapshot(prestige_game):
    from vatican_backend.application.commands import Checkpoint
    from vatican_backend.persistence.postgres.tables import GameServer
    import hashlib
    w=prestige_game;buy(w);drain(w)
    runs=w['client'].app.state.use_cases.runs;slot=w['slot'];release='sha256-test-release'
    runs.register_server(slot.server_id,slot.secret,ServerRegistration(release,3,'udp-loopback',None,1))
    pending=runs.assign(w['account'],respond_async=True,request_key=uuid4())
    assigned=runs.server_assignment(slot.server_id,slot.secret);identity=UUID(pending['runId'])
    runs.acknowledge(slot.server_id,slot.secret,Assignment(identity,assigned['generation']))
    payload=json.dumps({'schemaVersion':4,'runId':str(identity),'simulationTick':1,'contentReleaseId':release})
    runs.publish_checkpoint(slot.server_id,slot.secret,Checkpoint(identity,assigned['generation'],4,1,payload,hashlib.sha256(payload.encode()).hexdigest()))
    with w['db'].transaction() as session:session.get(GameServer,slot.server_id).last_seen=datetime.now(timezone.utc)-timedelta(seconds=20)
    with pytest.raises(ApplicationError,match='cannot apply'):runs.recover(slot.server_id,slot.secret,ServerRegistration(release,3,'udp-loopback'))
    recovered=runs.recover(slot.server_id,slot.secret,ServerRegistration(release,3,'udp-loopback',None,1))
    assert recovered['prestigeJson']==assigned['prestigeJson'] and recovered['generation']==assigned['generation']+1


def test_local_run_gets_separate_frozen_bonus_snapshot(prestige_game,local_game):
    from .test_local_runs import start
    w=prestige_game;buy(w);drain(w)
    reply=start(local_game)
    assert reply['prestigeBonuses']['modifiers'][0]['multiplier']=='1.050000'
    buy(w,level=1);drain(w)
    stored=local_game[1].get(w['account'],UUID(reply['runId']))
    assert stored['prestigeBonuses']==reply['prestigeBonuses']
    assert stored['weapons']==reply['weapons']


def test_max_level_and_missing_configuration_never_create_payment(prestige_game):
    w=prestige_game
    w['wallet'].receive(uuid4(),'run.currency_reward',1,{'accountId':str(w['account']),'runId':str(uuid4()),'amount':2000})
    for level in range(5):
        reply=buy(w,level=level);drain(w)
        assert state(w,reply)['status']=='COMPLETED'
    with pytest.raises(ApplicationError,match='maximum'):buy(w,level=5)
    with pytest.raises(ApplicationError):buy(w,code='missing')
    assert current(w)==5 and w['wallet'].balance(w['account'])['balance']==1000
    w['service'].settings=replace(w['service'].settings,prestige_ruleset_id='not-published')
    assert not w['service'].catalog(w['account'])['available']
    with pytest.raises(ApplicationError,match='unavailable'):buy(w,ruleset='not-published',code='wall.health')


@pytest.mark.parametrize('patch',[{'amount':True},{'amount':0},{'currencyId':'GEMS'},{'fromLevel':True},
    {'toLevel':5},{'accountId':'invalid'},{'rulesetId':''},{'code':'bad code'},{'extra':1}])
def test_wallet_rejects_malformed_payment_without_debit(prestige_game,patch):
    w=prestige_game;reply=buy(w)
    with w['db'].transaction() as session:message=session.get(OutboxMessage,UUID(reply['paymentMessageId']))
    with pytest.raises(WalletError) as error:w['wallet'].receive(uuid4(),PAYMENT_EVENT,1,{**message.payload,**patch})
    assert error.value.status_code==422
    assert w['wallet'].balance(w['account'])['balance']==500 and current(w)==0


def test_legacy_levels_are_visible_without_inventing_a_bonus_or_payment(prestige_game):
    w=prestige_game
    with w['db'].transaction() as session:
        session.add(PlayerPrestigeUpgrade(account_id=w['account'],code=CODE,level=2))
    catalog=w['service'].catalog(w['account'])
    assert catalog['requiresReview'] and catalog['legacyUpgrades']==[{'code':CODE,'level':2}]
    assert catalog['bonuses']['modifiers']==[]
    with pytest.raises(ApplicationError,match='Legacy'):buy(w,level=2)
    with w['db'].transaction() as session:assert session.scalar(select(func.count()).select_from(OutboxMessage))==0
