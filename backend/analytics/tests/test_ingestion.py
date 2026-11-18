from concurrent.futures import ThreadPoolExecutor
from copy import deepcopy
from datetime import datetime, timedelta, timezone
from uuid import UUID, uuid4
import pytest
from fastapi.testclient import TestClient
from sqlalchemy import func, select, text
from sqlalchemy.exc import IntegrityError
from vatican_analytics.application.errors import ApplicationError
from vatican_analytics.application.use_cases import AnalyticsUseCases, ProjectEvents
from vatican_analytics.bootstrap.settings import Settings
from vatican_analytics.domain.events import EVENT, parse, event_ids, receipt
from vatican_analytics.domain.projection import rows
from vatican_analytics.persistence.postgres.tables import AnalyticsInbox, ReceivedMessage
from vatican_analytics.presentation.app import create_app


def payload(**patch):
    return {'accountId':str(uuid4()),'runId':str(uuid4()),'rulesetId':'xp-v1','contentReleaseId':'sha256-test',
        'completedAt':datetime.now(timezone.utc).isoformat(),'outcome':'victory','churchLevel':1,
        'stats':{'heroKills':{'orc':4},'towerKills':{'orc':5},'buildingUpgrades':{'church':3}},
        'progression':{'xpGranted':80,'totalXp':80,'previousLevel':1,'currentLevel':3,
            'breakdown':{'heroKillXp':20,'towerKillXp':10,'tycoonXp':30,'victoryXp':20}},**patch}


def ready(actor):
    with actor.database.transaction() as session:
        for row in session.scalars(select(AnalyticsInbox).where(AnalyticsInbox.status=='PENDING')):
            row.available_at=datetime.now(timezone.utc)-timedelta(seconds=1)


@pytest.mark.parametrize('patch',[{'token':'private'},{'displayName':'player'}, {'accountId':'invalid'},
    {'churchLevel':True},{'completedAt':'2026-11-17T12:00:00'}, {'outcome':'unknown'},{'stats':{}},
    {'progression':{'xpGranted':True}}])
def test_invalid_or_private_fields_are_rejected_before_inbox_write(analytics_actor,patch):
    with pytest.raises(ApplicationError) as error:analytics_actor.api.receive(uuid4(),EVENT,1,payload(**patch))
    assert error.value.status_code==422
    with analytics_actor.database.transaction() as session:
        assert session.scalar(select(func.count()).select_from(AnalyticsInbox))==0


def test_progression_consistency_counter_bounds_and_unknown_nested_fields():
    base=payload()
    for change in ('sum','level','count','extra'):
        body=deepcopy(base)
        if change=='sum':body['progression']['breakdown']['victoryXp']=21
        if change=='level':body['progression']['currentLevel']=0
        if change=='count':body['stats']['heroKills']['orc']=True
        if change=='extra':body['progression']['token']='private'
        with pytest.raises(ValueError):parse(body)
    large=payload(stats={'heroKills':{str(i):2_147_483_647 for i in range(256)},'towerKills':{},'buildingUpgrades':{}})
    normalized=rows(large)
    assert normalized['fact_run_settlements'][0]['hero_kills']==256*2_147_483_647
    assert normalized['fact_xp_transactions'][0]['hero_mob_kills'] is None


def test_worker_projects_three_stable_events_and_replay_does_not_reprocess(analytics_actor):
    actor=analytics_actor;body=payload();identity=uuid4()
    assert actor.api.receive(identity,EVENT,1,body)['status']=='PENDING'
    assert actor.projection.calls==0
    assert actor.worker.once()
    final=actor.api.receive(identity,EVENT,1,body)
    assert final['status']=='APPLIED' and final['receipt']==receipt(body)
    assert len(final['receipt']['eventIds'])==3
    for _ in range(3):assert actor.api.receive(uuid4(),EVENT,1,body)['receipt']==final['receipt']
    assert not actor.worker.once() and actor.projection.calls==1
    assert len(actor.projection.tables['raw_game_events'])==3
    assert len(actor.projection.tables['fact_run_settlements'])==1


def test_source_and_message_alias_cannot_change_owner_or_payload(analytics_actor):
    actor=analytics_actor;body=payload();identity=uuid4()
    actor.api.receive(identity,EVENT,1,body)
    for patch in ({'accountId':str(uuid4())},{'outcome':'defeat'},{'rulesetId':'xp-v2'}):
        with pytest.raises(ApplicationError) as error:actor.api.receive(uuid4(),EVENT,1,{**body,**patch})
        assert error.value.status_code==409
    with pytest.raises(ApplicationError) as error:actor.api.receive(identity,EVENT,1,payload())
    assert error.value.status_code==409
    actor.worker.once()
    assert actor.projection.calls==1


def test_concurrent_aliases_create_one_source_and_parallel_workers_project_it_once(analytics_actor):
    actor=analytics_actor;body=payload()
    with ThreadPoolExecutor(max_workers=6) as pool:
        list(pool.map(lambda _:actor.api.receive(uuid4(),EVENT,1,body),range(12)))
        list(pool.map(lambda _:actor.worker.once(),range(6)))
    assert actor.projection.calls==1
    with actor.database.transaction() as session:
        assert session.scalar(select(func.count()).select_from(AnalyticsInbox))==1
        assert session.scalar(select(func.count()).select_from(ReceivedMessage))==12


def test_partial_projection_failure_retries_stable_keys_and_never_confirms_early(analytics_actor):
    actor=analytics_actor;body=payload();identity=uuid4()
    actor.api.receive(identity,EVENT,1,body)
    original=actor.projection.project
    def lost(payload):
        original(payload)
        raise RuntimeError('Lost warehouse response after writes')
    actor.projection.project=lost
    with pytest.raises(RuntimeError):actor.worker.once()
    assert actor.api.receive(identity,EVENT,1,body)['status']=='PENDING'
    assert len(actor.projection.tables['raw_game_events'])==3
    actor.projection.project=original;ready(actor);actor.worker.once()
    assert actor.api.receive(identity,EVENT,1,body)['status']=='APPLIED'
    assert len(actor.projection.tables['raw_game_events'])==3


def test_inbox_commit_failure_keeps_source_and_alias_atomic(analytics_actor):
    actor=analytics_actor
    with actor.database.engine.begin() as conn:
        conn.execute(text('ALTER TABLE received_messages ADD CONSTRAINT injected_alias_failure CHECK (false)'))
    try:
        with pytest.raises(IntegrityError):actor.api.receive(uuid4(),EVENT,1,payload())
        with actor.database.transaction() as session:
            assert session.scalar(select(func.count()).select_from(AnalyticsInbox))==0
    finally:
        with actor.database.engine.begin() as conn:conn.execute(text('ALTER TABLE received_messages DROP CONSTRAINT injected_alias_failure'))


def test_projection_confirmation_commit_failure_replays_without_changing_events(analytics_actor):
    actor=analytics_actor;body=payload();identity=uuid4()
    actor.api.receive(identity,EVENT,1,body)
    with actor.database.engine.begin() as conn:
        conn.execute(text("ALTER TABLE analytics_inbox ADD CONSTRAINT injected_projection_confirmation CHECK (status <> 'COMPLETED')"))
    try:
        with pytest.raises(IntegrityError):actor.worker.once()
        assert actor.api.receive(identity,EVENT,1,body)['status']=='PENDING'
    finally:
        with actor.database.engine.begin() as conn:conn.execute(text('ALTER TABLE analytics_inbox DROP CONSTRAINT injected_projection_confirmation'))
    ready(actor);actor.worker.once()
    assert len(actor.projection.tables['raw_game_events'])==3
    assert actor.api.receive(identity,EVENT,1,body)['status']=='APPLIED'


def test_projection_review_and_manual_retry_keep_immutable_source(analytics_actor):
    actor=analytics_actor;body=payload();identity=uuid4()
    actor.api.receive(identity,EVENT,1,body)
    with actor.database.transaction() as session:session.get(AnalyticsInbox,(EVENT,UUID(body['runId']))).attempts=19
    original=actor.projection.project
    actor.projection.project=lambda body:(_ for _ in ()).throw(RuntimeError('Warehouse down'))
    with pytest.raises(RuntimeError):actor.worker.once()
    assert actor.api.receive(identity,EVENT,1,body)['status']=='REVIEW'
    actor.projection.project=original
    assert actor.worker.requeue(UUID(body['runId']))
    actor.worker.once()
    assert actor.api.receive(identity,EVENT,1,body)['receipt']==receipt(body)


def test_stale_projection_lease_cannot_confirm_newer_attempt(analytics_actor):
    actor=analytics_actor;body=payload();actor.api.receive(uuid4(),EVENT,1,body)
    with actor.uow() as work:first=work.repository.claim()
    ready(actor)
    with actor.uow() as work:
        newer=work.repository.claim()
        assert not work.repository.confirm(first['runId'],first['lease'],receipt(body))
        assert work.repository.confirm(newer['runId'],newer['lease'],receipt(body))


def test_legacy_and_zero_xp_events_do_not_invent_progression():
    legacy=payload(progression=None)
    assert len(event_ids(legacy))==1 and set(rows(legacy))=={'raw_game_events','fact_run_settlements'}
    zero=payload(progression={'xpGranted':0,'totalXp':0,'previousLevel':1,'currentLevel':1,
        'breakdown':{'heroKillXp':0,'towerKillXp':0,'tycoonXp':0,'victoryXp':0}})
    assert len(event_ids(zero))==2 and rows(zero)['fact_xp_transactions'][0]['xp_delta']==0


def test_http_receiver_auth_pending_projection_ack_and_no_public_payload(analytics_storage,analytics_actor):
    db,url=analytics_storage;secret='s'*40
    body={'messageId':str(uuid4()),'eventName':EVENT,'schemaVersion':1,'payload':payload()}
    with TestClient(create_app(Settings(url,secret,'clickhouse+http://localhost:8123/test'))) as client:
        assert client.post('/internal/messages',json=body).status_code==401
        headers={'X-Service-Secret':secret}
        accepted=client.post('/internal/messages',json=body,headers=headers)
        assert accepted.status_code==202 and accepted.json()['status']=='PENDING'
        analytics_actor.worker.once()
        completed=client.post('/internal/messages',json=body,headers=headers)
        assert completed.status_code==200 and completed.json()['receipt']==receipt(body['payload'])
        assert client.post('/internal/messages',json={**body,'schemaVersion':True},headers=headers).status_code==422
        status='/internal/runs/'+body['payload']['runId']
        assert client.get(status).status_code==401
        assert 'payload' not in client.get(status,headers=headers).json()


def test_repeated_inbox_migration_preserves_pending_completed_sources_and_aliases(analytics_actor,analytics_storage):
    from vatican_analytics.migrate import migrate
    actor=analytics_actor;first=payload();second=payload()
    identity=uuid4()
    actor.api.receive(identity,EVENT,1,first);actor.worker.once()
    saved=actor.api.receive(uuid4(),EVENT,1,first)['receipt']
    actor.api.receive(uuid4(),EVENT,1,second)
    migrate(analytics_storage[1]);migrate(analytics_storage[1])
    assert actor.api.receive(identity,EVENT,1,first)['receipt']==saved
    assert actor.api.get(UUID(second['runId']))['status']=='PENDING'
    with actor.database.transaction() as session:
        assert session.scalar(select(func.count()).select_from(AnalyticsInbox))==2
        assert session.scalar(select(func.count()).select_from(ReceivedMessage))==3
