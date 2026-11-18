"""LOCAL lifecycle on PostgreSQL, including recovery between receipt and settlement."""
from concurrent.futures import ThreadPoolExecutor
from dataclasses import replace
from datetime import datetime, timezone
import json
from pathlib import Path
from uuid import UUID, uuid4

import pytest
from sqlalchemy import func, select
from vatican_backend.application.commands import ServerRegistration
from vatican_backend.application.errors import ApplicationError
from vatican_backend.application.local_runs import ConfigureLocalRuns
from vatican_backend.application.progression import ConfigureProgression
from vatican_backend.domain.local_runs import LocalPolicy, LocalResult
from vatican_backend.domain.progression import ProgressionRules, XPRule, LevelDefinition, RunStats
from vatican_backend.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory
from vatican_backend.persistence.postgres.tables import (ContentRelease, Run, RunContext, RunResultSubmission,
    RunValidationAudit, RunResultOperation, RunEnergyCharge, PlayerXPTransaction, OutboxMessage, Profile,
    LocalRunPolicy, XPRewardRule, RewardBundle, RewardBundleItem, PlayerLevelRewardClaim)
from .test_energy_lifecycle import FakeClock

AUTH={'Authorization':'Bearer test-token'}


@pytest.fixture
def local_game(backend):
    client, settings, slot=backend
    db=client.app.state.database
    account=UUID(client.app.state.test_player['accountId'])
    uow=PostgresUnitOfWorkFactory(db)
    bundle=uuid4()
    with db.transaction() as session:
        session.add(ContentRelease(id='local-content',schema_version=1,payload_json='{}',payload_sha256='0'*64))
        session.add(RewardBundle(id=bundle,code='local-level-two',version=1))
        session.flush()
        session.add(RewardBundleItem(bundle_id=bundle,item_index=0,reward_type='CURRENCY',reward_ref='PRESTIGE',quantity=10))
    ConfigureProgression(uow).execute(ProgressionRules('local-xp',
        (XPRule('HERO_KILL','*',5,100), XPRule('RUN_VICTORY','*',20,20)),
        (LevelDefinition(1,0),LevelDefinition(2,25,bundle))))
    value=json.loads((Path(__file__).parents[1]/'local-run-example.json').read_text())
    value.update(contentReleaseId='local-content',rulesetId='local-xp')
    ConfigureLocalRuns(uow).execute(LocalPolicy(value))
    service=client.app.state.use_cases.local_runs
    clock=FakeClock(datetime.now(timezone.utc))
    service.clock=client.app.state.use_cases.energy.clock=client.app.state.use_cases.runs.clock=clock
    return client,service,account,clock,value,slot


def start(world,key=None):
    return world[1].start(world[2],key or uuid4(),world[4]['policyId'])


def result(reply,**changes):
    body={k:reply[k] for k in ('sessionId','policyId','contentReleaseId','rulesetId','mapId','difficulty')}
    body.update(schemaVersion=1,outcome='victory',churchLevel=5,durationMs=60000,
                stats={'heroKills':{'orc':2},'towerKills':{},'buildingUpgrades':{}})
    body.update(changes)
    return LocalResult.from_snapshot(body)


def finish(world,reply,**changes):
    return world[1].finish(world[2],UUID(reply['runId']),result(reply,**changes))


def count(session,kind):return session.scalar(select(func.count()).select_from(kind))


def test_start_concurrent_retries_charge_once_and_freeze_context(local_game):
    client,service,account,clock,policy,_=local_game
    key=uuid4()
    with ThreadPoolExecutor(max_workers=5) as pool:
        replies=list(pool.map(lambda _:start(local_game,key),range(5)))
    assert len({r['runId'] for r in replies})==1
    assert sum(not r['replayed'] for r in replies)==1
    reply=replies[0]
    assert reply['simulationMode']=='LOCAL' and reply['energyCost']==5 and reply['status']=='ready'
    assert client.app.state.use_cases.energy.get(account)['currentEnergy']==95
    with client.app.state.database.transaction() as session:
        assert count(session,Run)==count(session,RunContext)==count(session,RunEnergyCharge)==1
        assert session.get(RunContext,UUID(reply['runId'])).validation_snapshot==policy
        assert session.get(Run,UUID(reply['runId'])).server_id is None
    with pytest.raises(ApplicationError,match='active run'):start(local_game)
    with pytest.raises(ApplicationError,match='payload or mode changed'):service.start(account,key,'changed')
    class Down:
        def snapshot(self,_):raise AssertionError('Replay must not read inventory')
    service.inventory=Down()
    assert start(local_game,key)['runId']==reply['runId']


def test_finish_concurrent_retries_settle_once(local_game):
    reply=start(local_game);local_game[3].advance(60)
    with ThreadPoolExecutor(max_workers=6) as pool:
        responses=list(pool.map(lambda _:finish(local_game,reply),range(6)))
    assert all(r['validationStatus']=='ACCEPTED' and r['settlementStatus']=='COMPLETED' for r in responses)
    assert sum(not r['replayed'] for r in responses)==1
    receipt=responses[0]['settlement']
    assert receipt['permanentReward']==125 and receipt['progression']['xpGranted']==30
    assert receipt['progression']['currentLevel']==2
    assert [gift['rewardedLevel'] for gift in receipt['progression']['unlockedRewards']]==[2]
    assert all(r['settlement']==receipt for r in responses)
    assert responses[0]['ratingStatus']==responses[0]['analyticsStatus']=='PENDING'
    with local_game[0].app.state.database.transaction() as session:
        for kind in (RunResultSubmission,RunValidationAudit,RunResultOperation,RunEnergyCharge,PlayerXPTransaction,PlayerLevelRewardClaim):
            assert count(session,kind)==1
        assert count(session,OutboxMessage)==4
        assert session.get(Profile,local_game[2]).version==2
        operation=session.get(RunResultOperation,UUID(reply['runId']))
        assert operation.server_id is None
    with pytest.raises(ApplicationError,match='payload changed'):finish(local_game,reply,churchLevel=4)


@pytest.mark.parametrize('changes,elapsed,reason',[
    ({'sessionId':str(uuid4())},60,'SessionMismatch'),
    ({'mapId':'other'},60,'ContextMismatch'),
    ({'contentReleaseId':'other'},60,'ContextMismatch'),
    ({'policyId':'other'},60,'ContextMismatch'),
    ({'rulesetId':'other'},60,'ContextMismatch'),
    ({'difficulty':'other'},60,'ContextMismatch'),
    ({},0,'DurationExceedsServerTime'),
    ({'durationMs':0},60,'DurationOutsideLimits'),
    ({'durationMs':1000},60,'VictoryTooEarly'),
    ({'churchLevel':4},60,'OutcomeMismatch'),
    ({'outcome':'defeat'},60,'OutcomeMismatch'),
    ({'stats':{'heroKills':{'unknown':1},'towerKills':{},'buildingUpgrades':{}}},60,'UnknownCounterTarget'),
    ({'stats':{'heroKills':{'orc':10001},'towerKills':{},'buildingUpgrades':{}}},60,'CounterLimitExceeded'),
    ({'stats':{'heroKills':{'orc':1300},'towerKills':{},'buildingUpgrades':{}}},60,'CounterRateExceeded'),
    ({},86400,'SubmissionExpired'),
])
def test_invalid_results_are_durably_rejected_without_rewards(local_game,changes,elapsed,reason):
    reply=start(local_game);local_game[3].advance(elapsed)
    rejected=finish(local_game,reply,**changes)
    assert rejected['validationStatus']=='REJECTED' and reason in rejected['validationReasons']
    assert rejected['settlementStatus']=='REJECTED' and rejected['settlement'] is None
    assert finish(local_game,reply,**changes)['replayed']
    with pytest.raises(ApplicationError,match='payload changed'):finish(local_game,reply,durationMs=59999)
    with local_game[0].app.state.database.transaction() as session:
        assert count(session,PlayerXPTransaction)==count(session,OutboxMessage)==count(session,RunResultOperation)==0
        assert count(session,RunValidationAudit)==count(session,RunEnergyCharge)==1


def test_settlement_failure_preserves_receipt_and_worker_resumes_after_deadline(local_game,monkeypatch):
    from vatican_backend.persistence.postgres.repositories.runs import RunsRepository
    reply=start(local_game);local_game[3].advance(60)
    original=RunsRepository.record_result
    def fail(*args,**kwargs):raise RuntimeError('injected settlement failure')
    monkeypatch.setattr(RunsRepository,'record_result',fail)
    with pytest.raises(RuntimeError,match='injected'):finish(local_game,reply)
    with local_game[0].app.state.database.transaction() as session:
        row=session.get(RunResultSubmission,UUID(reply['runId']))
        assert row.validation_status=='ACCEPTED' and row.settlement is None
        assert count(session,RunValidationAudit)==1
        assert count(session,PlayerXPTransaction)==count(session,OutboxMessage)==0
        assert session.get(Profile,local_game[2]).version==1
    with pytest.raises(ApplicationError,match='payload changed'):finish(local_game,reply,churchLevel=4)
    with pytest.raises(ApplicationError,match='cannot be abandoned'):local_game[1].abandon(local_game[2],UUID(reply['runId']))
    local_game[3].advance(86400)
    monkeypatch.setattr(RunsRepository,'record_result',original)
    assert local_game[1].maintain()==1
    recovered=finish(local_game,reply)
    assert recovered['replayed'] and recovered['settlement']['progression']['xpGranted']==30
    assert local_game[1].maintain()==0


def test_validation_audit_failure_rolls_back_submission(local_game,monkeypatch):
    from vatican_backend.persistence.postgres.repositories.local_runs import LocalRunsRepository
    reply=start(local_game);local_game[3].advance(60)
    original=LocalRunsRepository.record_submission
    def fail(*args,**kwargs):
        original(*args,**kwargs)
        raise RuntimeError('audit failure')
    monkeypatch.setattr(LocalRunsRepository,'record_submission',fail)
    with pytest.raises(RuntimeError):finish(local_game,reply)
    with local_game[0].app.state.database.transaction() as session:
        assert count(session,RunResultSubmission)==count(session,RunValidationAudit)==count(session,PlayerXPTransaction)==0
    monkeypatch.setattr(LocalRunsRepository,'record_submission',original)
    assert finish(local_game,reply)['settlementStatus']=='COMPLETED'


def test_expiry_and_abandon_never_refund_started_energy(local_game):
    reply=start(local_game);run_id=UUID(reply['runId'])
    assert local_game[1].abandon(local_game[2],run_id)['status']=='failed'
    assert local_game[1].abandon(local_game[2],run_id)['replayed']
    assert local_game[0].app.state.use_cases.energy.get(local_game[2])['currentEnergy']==95
    second=start(local_game);local_game[3].advance(86400)
    assert local_game[1].maintain()==1
    assert local_game[1].get(local_game[2],UUID(second['runId']))['status']=='failed'
    with local_game[0].app.state.database.transaction() as session:
        charges=list(session.scalars(select(RunEnergyCharge)))
        assert len(charges)==2 and all(c.refunded_at is None for c in charges)
    assert start(local_game)['status']=='ready'


def test_frozen_policy_and_xp_ignore_later_configuration_changes(local_game):
    reply=start(local_game);local_game[3].advance(60)
    with local_game[0].app.state.database.transaction() as session:
        row=session.get(LocalRunPolicy,local_game[4]['policyId'])
        row.snapshot={**row.snapshot,'prestigePerChurchLevel':999,'minVictoryDurationMs':70000}
        for rule in session.scalars(select(XPRewardRule).where(XPRewardRule.ruleset_id=='local-xp')):rule.xp_per_unit=999
    receipt=finish(local_game,reply)['settlement']
    assert receipt['permanentReward']==125 and receipt['progression']['xpGranted']==30


def test_defeat_awards_only_earned_xp(local_game):
    reply=start(local_game);local_game[3].advance(60)
    receipt=finish(local_game,reply,outcome='defeat',churchLevel=1)['settlement']
    assert receipt['permanentReward']==0 and receipt['progression']['xpGranted']==10


def test_owner_auth_strict_http_contract_and_local_has_no_server_grant(local_game):
    client,service,account,clock,policy,_=local_game
    assert client.post('/v1/local-runs',json={'requestKey':str(uuid4()),'policyId':policy['policyId']}).status_code==401
    response=client.post('/v1/local-runs',headers=AUTH,json={'requestKey':str(uuid4()),'policyId':policy['policyId']})
    assert response.status_code==200,response.text
    reply=response.json();path='/v1/local-runs/'+reply['runId']
    assert client.get('/v1/local-runs/active',headers=AUTH).json()['active']
    with pytest.raises(ApplicationError) as error:service.get(uuid4(),UUID(reply['runId']))
    assert error.value.status_code==404
    assert client.post('/v1/runs/'+reply['runId']+'/grant',headers=AUTH).status_code==409
    body=result(reply).snapshot()
    for fields in ({'accountId':str(uuid4())},{'permanentReward':999},{'durationMs':True},{'churchLevel':'5'},
                   {'stats':{'heroKills':{'orc':-1}}},{'schemaVersion':2}):
        assert client.post(path+'/finish',headers=AUTH,json={**body,**fields}).status_code==422
    clock.advance(60)
    accepted=client.post(path+'/finish',headers=AUTH,json=body)
    assert accepted.status_code==200,accepted.text
    assert accepted.json()['settlement']['progression']['xpGranted']==30
    assert client.get(path,headers=AUTH).json()['settlement']==accepted.json()['settlement']
    assert client.get('/v1/runs/'+reply['runId'],headers=AUTH).json()['simulationMode']=='LOCAL'


def test_dedicated_and_local_share_start_key_namespace(local_game):
    client,service,account,clock,policy,slot=local_game
    key=uuid4();reply=start(local_game,key)
    service.abandon(account,UUID(reply['runId']))
    runs=client.app.state.use_cases.runs
    with pytest.raises(ApplicationError,match='mode changed'):runs.assign(account,respond_async=True,request_key=key)
    runs.register_server(slot.server_id,slot.secret,ServerRegistration('sha256-test-release',3,'udp-loopback'))
    dedicated_key=uuid4();runs.assign(account,respond_async=True,request_key=dedicated_key)
    with pytest.raises(ApplicationError,match='mode changed'):service.start(account,dedicated_key,policy['policyId'])


def test_insufficient_energy_rolls_back_run_context_and_key(local_game):
    from vatican_backend.persistence.postgres.tables import PlayerEnergy,IdempotencyRecord
    client,service,account,*_=local_game
    client.app.state.use_cases.energy.get(account)
    with client.app.state.database.transaction() as session:session.get(PlayerEnergy,account).current_energy=0
    with pytest.raises(ApplicationError):start(local_game)
    with client.app.state.database.transaction() as session:
        assert count(session,Run)==count(session,RunContext)==count(session,RunEnergyCharge)==count(session,IdempotencyRecord)==0


@pytest.mark.parametrize('destination',['wallet','inventory'])
@pytest.mark.parametrize('lost_ack',[False,True])
def test_partial_remote_failure_and_lost_ack_never_repeat_rewards(local_game,wallet_storage,inventory_storage,destination,lost_ack):
    from datetime import timedelta
    from vatican_backend.application.messaging import DeliverRewards
    from vatican_wallet.application.use_cases import WalletUseCases
    from vatican_wallet.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as WalletUow
    from vatican_inventory.application.use_cases import InventoryUseCases
    from vatican_inventory.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as InventoryUow
    client,service,account,clock,*_=local_game
    class Definitions:
        def get(self):return client.app.state.use_cases.weapons.get()
    wallet=WalletUseCases(WalletUow(wallet_storage[0]))
    inventory=InventoryUseCases(InventoryUow(inventory_storage[0]),Definitions())
    initial_balance=wallet.balance(account)['balance']
    initial_weapons=len(inventory.get(account)['Weapons'])
    reply=start(local_game);clock.advance(60)
    settled=finish(local_game,reply)['settlement']
    # Exercise wallet/inventory independently of the two additional consumers.
    with client.app.state.database.transaction() as session:
        for row in session.scalars(select(OutboxMessage).where(OutboxMessage.destination.in_(('rating','analytics')))):
            row.available_at=datetime.now(timezone.utc)+timedelta(days=1)
    class Gateway:
        broken=True
        def deliver(self,message):
            participant={'wallet':wallet,'inventory':inventory}[message['destination']]
            fail=self.broken and message['destination']==destination
            if fail and not lost_ack:raise ApplicationError(503,'Participant unavailable')
            ack=participant.receive(message['id'],message['event_name'],message['schema_version'],message['payload'])
            if fail:raise ApplicationError(503,'Acknowledgement lost')
            return ack.get('receipt')
    gateway=Gateway();worker=DeliverRewards(service.uow,gateway)
    while worker.once():pass
    partial=service.get(account,UUID(reply['runId']))
    assert partial['rewardStatus']=='PENDING' and partial['settlement']==settled
    with client.app.state.database.transaction() as session:
        messages=list(session.scalars(select(OutboxMessage).where(OutboxMessage.destination.in_(('wallet','inventory')))))
        assert sum(m.published_at is not None for m in messages)==1
        for message in messages:
            if message.published_at is None:message.available_at=datetime.now(timezone.utc)-timedelta(seconds=1)
    gateway.broken=False
    while worker.once():pass
    assert finish(local_game,reply)['rewardStatus']=='COMPLETED'
    assert wallet.balance(account)['balance']==initial_balance+125
    assert len(inventory.get(account)['Weapons'])==initial_weapons+1
    with client.app.state.database.transaction() as session:
        assert count(session,PlayerXPTransaction)==count(session,RunEnergyCharge)==1
    # A different transport envelope still resolves the same business run source.
    with client.app.state.database.transaction() as session:
        messages=[(m.destination,m.event_name,m.schema_version,m.payload) for m in session.scalars(select(OutboxMessage).where(OutboxMessage.destination.in_(('wallet','inventory'))))]
    for dest,event,version,payload in messages:
        {'wallet':wallet,'inventory':inventory}[dest].receive(uuid4(),event,version,payload)
    assert wallet.balance(account)['balance']==initial_balance+125
    assert len(inventory.get(account)['Weapons'])==initial_weapons+1


def test_configuration_is_explicit_immutable_and_requires_published_dependencies(local_game):
    client,service,account,clock,policy,_=local_game
    configure=ConfigureLocalRuns(service.uow)
    assert configure.execute(LocalPolicy(policy))['replayed']
    with pytest.raises(ApplicationError,match='immutable'):
        configure.execute(LocalPolicy({**policy,'prestigePerChurchLevel':99}))
    for fields in ({'contentReleaseId':'missing'},{'rulesetId':'missing'},{'rewardWeaponConfigurationId':'missing'}):
        with pytest.raises(ApplicationError):configure.execute(LocalPolicy({**policy,'policyId':'new',**fields}))
    with client.app.state.database.transaction() as session:assert count(session,LocalRunPolicy)==1


def test_expired_local_does_not_block_dedicated_assignment(local_game):
    client,service,account,clock,policy,slot=local_game
    reply=start(local_game);clock.advance(86400)
    runs=client.app.state.use_cases.runs
    runs.register_server(slot.server_id,slot.secret,ServerRegistration('sha256-test-release',3,'udp-loopback'))
    pending=runs.assign(account,respond_async=True,request_key=uuid4())
    assert pending['runId']!=reply['runId']
    assert service.get(account,UUID(reply['runId']))['status']=='failed'


def test_clock_rollback_and_legacy_context_cannot_award_rewards(local_game):
    reply=start(local_game);local_game[3].advance(-1)
    with pytest.raises(ApplicationError) as error:finish(local_game,reply)
    assert error.value.status_code==503
    with local_game[0].app.state.database.transaction() as session:
        assert count(session,RunResultSubmission)==0
        session.get(RunContext,UUID(reply['runId'])).validation_snapshot=None
    local_game[3].advance(61)
    with pytest.raises(ApplicationError,match='Legacy'):finish(local_game,reply)
    assert local_game[1].abandon(local_game[2],UUID(reply['runId']))['status']=='failed'


def test_exhausted_reward_delivery_can_be_requeued_without_resettling(local_game):
    from vatican_backend.application.messaging import DeliverRewards
    reply=start(local_game);local_game[3].advance(60)
    settled=finish(local_game,reply)['settlement']
    with local_game[0].app.state.database.transaction() as session:
        for row in session.scalars(select(OutboxMessage)):row.attempts=19
    class Gateway:
        broken=True
        def deliver(self,message):
            if self.broken:raise ApplicationError(503,'Unavailable')
    gateway=Gateway();worker=DeliverRewards(local_game[1].uow,gateway)
    while worker.once():pass
    assert finish(local_game,reply)['rewardStatus']=='FAILED'
    with local_game[0].app.state.database.transaction() as session:
        ids=[m.id for m in session.scalars(select(OutboxMessage))]
    gateway.broken=False
    for identity in ids:assert worker.requeue(identity)
    while worker.once():pass
    response=finish(local_game,reply)
    assert response['rewardStatus']=='COMPLETED' and response['settlement']==settled
    with local_game[0].app.state.database.transaction() as session:
        assert count(session,PlayerXPTransaction)==count(session,RunEnergyCharge)==count(session,RunValidationAudit)==1
