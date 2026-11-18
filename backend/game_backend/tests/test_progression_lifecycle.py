from concurrent.futures import ThreadPoolExecutor
from dataclasses import replace
import hashlib
import json
from uuid import UUID,uuid4

import pytest
from sqlalchemy import func, select, text
from vatican_backend.application.commands import Assignment, RunResult, ServerRegistration
from vatican_backend.application.errors import ApplicationError
from vatican_backend.application.progression import ConfigureProgression
from vatican_backend.bootstrap.settings import RewardPolicy
from vatican_backend.domain.progression import (LevelDefinition, ProgressionRules, RunStats, XPRule, MAX_XP)
from vatican_backend.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory
from vatican_backend.persistence.postgres.tables import (OutboxMessage, PlayerLevelProgress, PlayerLevelRewardClaim,
    PlayerXPTransaction, Profile, ProgressionConfiguration, RewardBundle, RewardBundleItem,
    Run, RunResultOperation, RunXPBreakdown, XPRewardRule)
from .support import player_for, server_header

RELEASE='sha256-progression-test'
STATS=RunStats((('orc',4),),(('orc',5),),(('church',3),))


@pytest.fixture
def xp_game(backend):
    client,settings,slot=backend
    account=UUID(player_for(client)['accountId'])
    two,three=uuid4(),uuid4()
    with client.app.state.database.transaction() as session:
        session.add_all([RewardBundle(id=two,code='level-2',version=1),RewardBundle(id=three,code='level-3',version=1)])
        session.flush()
        session.add_all([RewardBundleItem(bundle_id=two,item_index=0,reward_type='CURRENCY',reward_ref='PRESTIGE',quantity=10),
                         RewardBundleItem(bundle_id=three,item_index=0,reward_type='WEAPON',reward_ref='sword.common',quantity=1)])
    rules=ProgressionRules(RELEASE,(XPRule('HERO_KILL','*',5,50),XPRule('TOWER_KILL','*',2,20),
        XPRule('BUILDING_UPGRADE','*',10,40),XPRule('RUN_VICTORY','*',20,20)),
        (LevelDefinition(1,0),LevelDefinition(2,25,two),LevelDefinition(3,60,three),LevelDefinition(4,100)))
    ConfigureProgression(PostgresUnitOfWorkFactory(client.app.state.database)).execute(rules)
    client.app.state.use_cases.runs.settings=replace(settings,reward_policies={**settings.reward_policies,RELEASE:RewardPolicy(25)})
    return client,slot,account,rules


def start(world,stats=STATS,outcome='victory',release=RELEASE):
    client,slot,account,_=world
    runs=client.app.state.use_cases.runs
    runs.register_server(slot.server_id,slot.secret,ServerRegistration(release,3,'udp-loopback'))
    pending=runs.assign(account,respond_async=True,request_key=uuid4())
    assignment=runs.server_assignment(slot.server_id,slot.secret)
    run_id=UUID(pending['runId'])
    runs.acknowledge(slot.server_id,slot.secret,Assignment(run_id,assignment['generation']))
    return RunResult(run_id,assignment['generation'],account,release,outcome,1,stats)


def apply(world,result):
    return world[0].app.state.use_cases.runs.apply_result(world[1].server_id,world[1].secret,result)


def state(world):
    return world[0].app.state.use_cases.progression.get(world[2])


def count(session,type_):
    return session.scalar(select(func.count()).select_from(type_))


def test_settlement_awards_xp_all_crossed_levels_and_claims_in_one_transaction(xp_game):
    result=start(xp_game)
    reply=apply(xp_game,result)
    xp=reply['progression']
    assert xp['xpGranted']==xp['totalXp']==80
    assert xp['previousLevel']==1 and xp['currentLevel']==3
    assert xp['breakdown']=={'heroKillXp':20,'towerKillXp':10,'tycoonXp':30,'victoryXp':20}
    assert [gift['rewardedLevel'] for gift in xp['unlockedRewards']]==[2,3]
    current=state(xp_game)
    assert current['totalXp']==80 and current['currentLevel']==3 and current['nextLevelTotalXp']==100
    assert current['availableRewards']==xp['unlockedRewards']
    with xp_game[0].app.state.database.transaction() as session:
        assert count(session,PlayerXPTransaction)==count(session,RunXPBreakdown)==1
        assert count(session,PlayerLevelRewardClaim)==2
        assert count(session,OutboxMessage)==4
        rating=session.scalar(select(OutboxMessage).where(OutboxMessage.destination=="rating"))
        assert rating.payload["points"]==xp["xpGranted"] and rating.payload["runId"]==str(result.run_id)
        assert session.get(Profile,xp_game[2]).version==2
        stored=session.get(RunResultOperation,result.run_id)
        assert stored.progression==xp
        assert stored.result_payload['stats']==STATS.snapshot()


def test_concurrent_retries_return_one_durable_xp_award_and_one_gift_per_level(xp_game):
    result=start(xp_game)
    with ThreadPoolExecutor(max_workers=6) as workers:
        replies=list(workers.map(lambda _:apply(xp_game,result),range(6)))
    assert sum(not reply['replayed'] for reply in replies)==1
    assert all(reply['progression']==replies[0]['progression'] for reply in replies)
    assert state(xp_game)['totalXp']==80
    with xp_game[0].app.state.database.transaction() as session:
        assert count(session,PlayerXPTransaction)==count(session,RunXPBreakdown)==1
        assert count(session,PlayerLevelRewardClaim)==2
        assert session.get(Profile,xp_game[2]).version==2


def test_changed_payload_cannot_add_more_xp_to_an_existing_run(xp_game):
    result=start(xp_game)
    apply(xp_game,result)
    for changed in (replace(result,stats=replace(STATS,hero_kills=(('orc',5),))),replace(result,outcome='defeat')):
        with pytest.raises(ApplicationError,match='Conflicting result'):
            apply(xp_game,changed)
    assert state(xp_game)['totalXp']==80


def test_counter_order_does_not_change_request_identity(xp_game):
    result=start(xp_game,RunStats((('orc',1),('skeleton',2))))
    first=apply(xp_game,result)
    replay=apply(xp_game,replace(result,stats=RunStats((('skeleton',2),('orc',1)))))
    assert replay['replayed'] and replay['progression']==first['progression']


def test_zero_xp_receipt_still_prevents_later_counter_replacement(xp_game):
    result=start(xp_game,RunStats(),outcome='defeat')
    first=apply(xp_game,result)
    assert first['progression']['xpGranted']==0
    assert state(xp_game)['currentLevel']==1
    assert apply(xp_game,result)['replayed']
    with pytest.raises(ApplicationError,match='Conflicting result'):
        apply(xp_game,replace(result,stats=STATS))
    with xp_game[0].app.state.database.transaction() as session:
        assert count(session,PlayerXPTransaction)==1
        assert count(session,PlayerLevelRewardClaim)==0


def test_defeat_grants_only_earned_combat_and_building_xp(xp_game):
    reply=apply(xp_game,start(xp_game,outcome='defeat'))
    assert reply['progression']['xpGranted']==60
    assert reply['progression']['breakdown']['victoryXp']==0
    assert reply['permanentReward']==0


def test_subsequent_run_accumulates_xp_and_old_receipts_remain_unchanged(xp_game):
    first_result=start(xp_game)
    first=apply(xp_game,first_result)
    second_result=start(xp_game)
    with ThreadPoolExecutor(max_workers=2) as workers:
        old=workers.submit(apply,xp_game,first_result)
        new=workers.submit(apply,xp_game,second_result)
        assert old.result()['progression']==first['progression']
        second=new.result()
    assert second['progression']['totalXp']==160 and second['progression']['currentLevel']==4
    assert second['progression']['unlockedRewards']==[]
    assert state(xp_game)['totalXp']==160
    assert len(state(xp_game)['availableRewards'])==2
    assert apply(xp_game,first_result)['progression']['totalXp']==80


def test_result_record_failure_rolls_back_xp_levels_claims_rewards_and_run(xp_game,monkeypatch):
    from vatican_backend.persistence.postgres.repositories.runs import RunsRepository
    result=start(xp_game)
    original=RunsRepository.record_result
    def fail(*args,**kwargs):
        raise RuntimeError('Injected result failure')
    monkeypatch.setattr(RunsRepository,'record_result',fail)
    with pytest.raises(RuntimeError,match='Injected result failure'):
        apply(xp_game,result)
    with xp_game[0].app.state.database.transaction() as session:
        assert count(session,PlayerLevelProgress)==count(session,PlayerXPTransaction)==count(session,RunXPBreakdown)==0
        assert count(session,PlayerLevelRewardClaim)==count(session,OutboxMessage)==0
        assert session.get(Run,result.run_id).status=='ready'
        assert session.get(Profile,xp_game[2]).version==1
    monkeypatch.setattr(RunsRepository,'record_result',original)
    assert apply(xp_game,result)['progression']['xpGranted']==80


def test_xp_ledger_failure_rolls_back_all_changes_and_http_error_does_not_leak_sql(xp_game):
    result=start(xp_game)
    client,slot,_,_=xp_game
    with client.app.state.database.engine.begin() as connection:
        connection.execute(text('ALTER TABLE player_xp_transactions ADD CONSTRAINT injected_xp_failure CHECK (xp_delta = 0)'))
    try:
        response=client.post(f'/internal/servers/{slot.server_id}/results',headers=server_header(slot),json={
            'runId':str(result.run_id),'accountId':str(result.account_id),'contentReleaseId':result.content_release_id,
            'generation':result.generation,'outcome':result.outcome,'churchLevel':1,'stats':STATS.snapshot()})
        assert response.status_code==500
        assert 'INSERT' not in response.text and 'injected_xp_failure' not in response.text
        assert state(xp_game)['totalXp']==0
        with client.app.state.database.transaction() as session:
            assert count(session,RunXPBreakdown)==count(session,PlayerXPTransaction)==count(session,PlayerLevelRewardClaim)==0
            assert session.get(Run,result.run_id).status=='ready'
    finally:
        with client.app.state.database.engine.begin() as connection:
            connection.execute(text('ALTER TABLE player_xp_transactions DROP CONSTRAINT injected_xp_failure'))
    assert apply(xp_game,result)['progression']['totalXp']==80


def test_rules_are_immutable_and_the_running_match_uses_its_pinned_snapshot(xp_game):
    result=start(xp_game)
    client,_,_,rules=xp_game
    configure=ConfigureProgression(PostgresUnitOfWorkFactory(client.app.state.database))
    configure.execute(replace(rules,rules=tuple(reversed(rules.rules))))
    with pytest.raises(ApplicationError,match='immutable'):
        configure.execute(replace(rules,rules=(XPRule('RUN_VICTORY','*',999,999),)))
    with client.app.state.database.transaction() as session:
        for row in session.scalars(select(XPRewardRule).where(XPRewardRule.ruleset_id==RELEASE)):
            row.xp_per_unit=999
    assert apply(xp_game,result)['progression']['xpGranted']==80


def test_xp_capacity_cannot_overflow_the_balance(xp_game):
    result=start(xp_game)
    client,_,account,_=xp_game
    with client.app.state.database.transaction() as session:
        session.add(PlayerLevelProgress(account_id=account,total_xp=MAX_XP-10,current_level=4))
    with pytest.raises(ApplicationError,match='capacity exceeded'):
        apply(xp_game,result)
    with client.app.state.database.transaction() as session:
        assert session.get(PlayerLevelProgress,account).total_xp==MAX_XP-10
        assert session.get(Run,result.run_id).status=='ready'
        assert count(session,PlayerXPTransaction)==count(session,OutboxMessage)==0


def test_bad_owner_or_stale_generation_never_grants_xp(xp_game):
    result=start(xp_game)
    for changed in (replace(result,account_id=uuid4()),replace(result,generation=result.generation+1)):
        with pytest.raises(ApplicationError):
            apply(xp_game,changed)
    assert state(xp_game)['totalXp']==0


def test_progression_api_resolves_owner_from_auth_and_reports_the_saved_run_progression(xp_game):
    client,_,account,_=xp_game
    assert client.get('/v1/progression').status_code==401
    headers={'Authorization':'Bearer test-token'}
    initial=client.get('/v1/progression',headers=headers).json()
    assert initial['accountId']==str(account) and initial['currentLevel']==1 and initial['totalXp']==0
    result=start(xp_game)
    reply=apply(xp_game,result)
    current=client.get('/v1/progression?accountId='+str(uuid4()),headers=headers)
    assert current.status_code==200 and current.json()['accountId']==str(account)
    assert current.json()['totalXp']==80
    completed=client.get('/v1/runs/'+str(result.run_id),headers=headers)
    assert completed.json()['progression']==reply['progression']


@pytest.mark.parametrize('stats',[{'heroKills':{'orc':True}},{'heroKills':{'orc':-1}},{'heroKills':{'orc':'4'}},
    {'heroKills':{'orc':1.5}},{'heroKills':{'orc':2**40}},{'xpGranted':99999}])
def test_http_rejects_client_supplied_xp_or_invalid_counters(xp_game,stats):
    result=start(xp_game)
    client,slot,_,_=xp_game
    response=client.post(f'/internal/servers/{slot.server_id}/results',headers=server_header(slot),json={
        'runId':str(result.run_id),'accountId':str(result.account_id),'contentReleaseId':RELEASE,
        'generation':result.generation,'outcome':'victory','churchLevel':1,'stats':stats})
    assert response.status_code==422
    assert state(xp_game)['totalXp']==0


def test_legacy_inflight_runs_do_not_receive_unpinned_xp_and_old_hashes_can_be_replayed(xp_game):
    result=start(xp_game,RunStats())
    client=xp_game[0]
    with client.app.state.database.transaction() as session:
        session.get(Run,result.run_id).xp_rules_snapshot=None
    first=apply(xp_game,result)
    assert first['progression'] is None
    legacy={'runId':str(result.run_id),'accountId':str(result.account_id),'contentReleaseId':RELEASE,
            'generation':result.generation,'outcome':'victory','churchLevel':1}
    with client.app.state.database.transaction() as session:
        session.get(RunResultOperation,result.run_id).payload_hash=hashlib.sha256(json.dumps(legacy,sort_keys=True,separators=(',',':')).encode()).digest()
    assert apply(xp_game,result)['replayed']
    with pytest.raises(ApplicationError,match='Conflicting result'):
        apply(xp_game,replace(result,stats=STATS))
    with client.app.state.database.transaction() as session:
        assert count(session,PlayerXPTransaction)==0


def test_positive_result_over_http_returns_xp_and_cannot_be_posted_by_a_client(xp_game):
    result=start(xp_game)
    client,slot,_,_=xp_game
    body={'runId':str(result.run_id),'accountId':str(result.account_id),'contentReleaseId':RELEASE,
          'generation':result.generation,'outcome':'victory','churchLevel':1,'stats':STATS.snapshot()}
    endpoint=f'/internal/servers/{slot.server_id}/results'
    assert client.post(endpoint,headers={'Authorization':'Bearer test-token'},json=body).status_code==401
    response=client.post(endpoint,headers=server_header(slot),json=body)
    assert response.status_code==200 and response.json()['progression']['xpGranted']==80
    replay=client.post(endpoint,headers=server_header(slot),json=body)
    assert replay.json()['replayed'] and replay.json()['progression']==response.json()['progression']


def test_policy_switches_do_not_reset_xp_level_or_reopen_earlier_gifts(xp_game):
    apply(xp_game,start(xp_game))
    client,_,account,rules=xp_game
    alternate='sha256-progression-alternate'
    extra_bundle=uuid4()
    with client.app.state.database.transaction() as session:
        session.add(RewardBundle(id=extra_bundle,code='level-4',version=1))
    changed=ProgressionRules(alternate,rules.rules,(LevelDefinition(1,0),
        LevelDefinition(2,10,extra_bundle),LevelDefinition(3,30,extra_bundle),LevelDefinition(4,100,extra_bundle)))
    ConfigureProgression(PostgresUnitOfWorkFactory(client.app.state.database)).execute(changed)
    service=client.app.state.use_cases.runs
    service.settings=replace(service.settings,reward_policies={**service.settings.reward_policies,alternate:RewardPolicy(25)})
    second=apply(xp_game,start(xp_game,RunStats(),release=alternate))
    assert second['progression']['totalXp']==100 and second['progression']['currentLevel']==4
    assert [g['rewardedLevel'] for g in second['progression']['unlockedRewards']]==[4]
    third=apply(xp_game,start(xp_game,RunStats()))
    assert third['progression']['totalXp']==120 and third['progression']['currentLevel']==4
    assert third['progression']['unlockedRewards']==[]
    with client.app.state.database.transaction() as session:
        assert count(session,PlayerLevelRewardClaim)==3
        assert {r.ruleset_id for r in session.scalars(select(PlayerLevelRewardClaim).where(
            PlayerLevelRewardClaim.rewarded_level.in_([2,3])))}=={RELEASE}


def test_missing_progression_policy_does_not_create_a_run_or_consume_energy(xp_game):
    client,slot,account,_=xp_game
    service=client.app.state.use_cases.runs
    missing='sha256-without-progression'
    service.settings=replace(service.settings,reward_policies={missing:RewardPolicy(25)})
    service.register_server(slot.server_id,slot.secret,ServerRegistration(missing,3,'udp-loopback'))
    energy_before=client.app.state.use_cases.energy.get(account)['currentEnergy']
    with pytest.raises(ApplicationError) as error:
        service.assign(account,respond_async=True,request_key=uuid4())
    assert error.value.code=='ProgressionRulesUnavailable'
    assert client.app.state.use_cases.energy.get(account)['currentEnergy']==energy_before
    with client.app.state.database.transaction() as session:
        assert count(session,Run)==0


def test_configuration_rejects_missing_reward_bundle_and_keeps_existing_policy(xp_game):
    client,_,_,rules=xp_game
    configure=ConfigureProgression(PostgresUnitOfWorkFactory(client.app.state.database))
    invalid=replace(rules,ruleset_id='missing-bundle',levels=(LevelDefinition(1,0),LevelDefinition(2,25,uuid4())))
    with pytest.raises(ApplicationError,match='bundle does not exist'):
        configure.execute(invalid)
    with client.app.state.database.transaction() as session:
        assert session.get(ProgressionConfiguration,'missing-bundle') is None
        assert session.get(ProgressionConfiguration,RELEASE).payload_hash==rules.digest()


def test_changed_counters_are_rejected_even_when_the_same_cap_would_be_awarded(xp_game):
    result=start(xp_game,RunStats((('orc',100),)))
    first=apply(xp_game,result)
    with pytest.raises(ApplicationError,match='Conflicting result'):
        apply(xp_game,replace(result,stats=RunStats((('orc',200),))))
    assert state(xp_game)['totalXp']==first['progression']['totalXp']


def test_database_constraints_independently_reject_second_xp_ledger_entry(xp_game):
    from sqlalchemy.exc import IntegrityError
    result=start(xp_game)
    apply(xp_game,result)
    with pytest.raises(IntegrityError):
        with xp_game[0].app.state.database.transaction() as session:
            session.add(PlayerXPTransaction(account_id=xp_game[2],run_id=result.run_id,xp_delta=80))
    assert state(xp_game)['totalXp']==80


def test_async_reward_delivery_failure_does_not_repeat_or_rollback_committed_xp(xp_game):
    result=start(xp_game)
    first=apply(xp_game,result)
    class Down:
        def deliver(self,message):
            raise ApplicationError(503,'Reward participant unavailable')
    rewards=xp_game[0].app.state.use_cases.rewards
    rewards.gateway=Down()
    assert rewards.once()
    replay=apply(xp_game,result)
    assert replay['rewardStatus']=='PENDING' and replay['progression']==first['progression']
    assert state(xp_game)['totalXp']==80
    with xp_game[0].app.state.database.transaction() as session:
        assert count(session,PlayerXPTransaction)==1 and count(session,PlayerLevelRewardClaim)==2


def test_xp_version_can_be_selected_independently_of_content_and_remains_pinned(xp_game):
    client,slot,account,rules=xp_game
    separate=replace(rules,ruleset_id='xp-balance-v2',rules=(XPRule('RUN_VICTORY','*',45,45),))
    ConfigureProgression(PostgresUnitOfWorkFactory(client.app.state.database)).execute(separate)
    service=client.app.state.use_cases.runs
    service.register_server(slot.server_id,slot.secret,ServerRegistration(RELEASE,3,'udp-loopback','xp-balance-v2'))
    pending=service.assign(account,respond_async=True,request_key=uuid4())
    assignment=service.server_assignment(slot.server_id,slot.secret)
    assert assignment['contentReleaseId']==RELEASE and assignment['rulesetId']=='xp-balance-v2'
    run_id=UUID(pending['runId'])
    service.acknowledge(slot.server_id,slot.secret,Assignment(run_id,assignment['generation']))
    reply=service.apply_result(slot.server_id,slot.secret,RunResult(run_id,assignment['generation'],account,RELEASE,'victory',1))
    assert reply['progression']['xpGranted']==45 and reply['progression']['rulesetId']=='xp-balance-v2'
    with client.app.state.database.transaction() as session:
        assert session.get(RunXPBreakdown,run_id).ruleset_id=='xp-balance-v2'


def test_recovery_cannot_switch_the_pinned_ruleset(xp_game):
    import hashlib,json
    from datetime import datetime,timedelta,timezone
    from vatican_backend.application.commands import Checkpoint
    from vatican_backend.persistence.postgres.tables import GameServer
    result=start(xp_game)
    client,slot,_,rules=xp_game
    service=client.app.state.use_cases.runs
    payload=json.dumps({'schemaVersion':4,'runId':str(result.run_id),'simulationTick':1,'contentReleaseId':RELEASE})
    service.publish_checkpoint(slot.server_id,slot.secret,Checkpoint(result.run_id,result.generation,4,1,payload,
        hashlib.sha256(payload.encode()).hexdigest()))
    with client.app.state.database.transaction() as session:
        session.get(GameServer,slot.server_id).last_seen=datetime.now(timezone.utc)-timedelta(seconds=20)
    with pytest.raises(ApplicationError,match='Recovery content or server slot changed'):
        service.recover(slot.server_id,slot.secret,ServerRegistration(RELEASE,3,'udp-loopback','other-xp-version'))
    recovered=service.recover(slot.server_id,slot.secret,ServerRegistration(RELEASE,3,'udp-loopback',RELEASE))
    assert recovered['rulesetId']==RELEASE


def test_bound_xp_policy_cannot_be_used_with_different_content(xp_game):
    from vatican_backend.persistence.postgres.tables import ContentRelease,Ruleset
    client,slot,_,rules=xp_game
    with client.app.state.database.transaction() as session:
        session.add(ContentRelease(id='other-content',schema_version=1,payload_json='{}',payload_sha256='0'*64))
        session.flush()
        session.get(Ruleset,RELEASE).content_release_id='other-content'
    with pytest.raises(ApplicationError) as error:
        client.app.state.use_cases.runs.register_server(slot.server_id,slot.secret,ServerRegistration(RELEASE,3,'udp-loopback',RELEASE))
    assert error.value.code=='ProgressionRulesUnavailable'
