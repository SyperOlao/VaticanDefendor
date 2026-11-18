"""Confirmed LOCAL settlements through the real rating and analytics consumers."""
from concurrent.futures import ThreadPoolExecutor
from dataclasses import replace
from datetime import datetime,timedelta,timezone
from uuid import UUID,uuid4

import pytest
from sqlalchemy import select,func,text
from sqlalchemy.exc import IntegrityError
from vatican_backend.application.errors import ApplicationError
from vatican_backend.application.messaging import DeliverRewards
from vatican_backend.domain.analytics import analytics_message_id,expected_receipt
from vatican_backend.domain.rating import rating_message_id
from vatican_backend.persistence.postgres.tables import (OutboxMessage,PlayerXPTransaction,RunEnergyCharge,
    RunResultSubmission,RunValidationAudit,Profile)
from vatican_rating.application.use_cases import RatingUseCases
from vatican_rating.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as RatingUow
from vatican_rating.persistence.postgres.tables import RunScoreReceipt
from vatican_analytics.persistence.postgres.tables import AnalyticsInbox
from vatican_wallet.application.use_cases import WalletUseCases
from vatican_wallet.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as WalletUow
from vatican_inventory.application.use_cases import InventoryUseCases
from vatican_inventory.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as InventoryUow
from .test_local_runs import local_game,start,finish,AUTH


@pytest.fixture
def world(local_game,wallet_storage,inventory_storage,rating_storage,analytics_actor):
    client,service,account,clock,*_=local_game
    class Definitions:
        def get(self):return client.app.state.use_cases.weapons.get()
    return {'local':local_game,'client':client,'service':service,'account':account,'clock':clock,
        'db':client.app.state.database,'wallet':WalletUseCases(WalletUow(wallet_storage[0])),
        'inventory':InventoryUseCases(InventoryUow(inventory_storage[0]),Definitions()),
        'rating':RatingUseCases(RatingUow(rating_storage[0])),'rating_db':rating_storage[0],
        'analytics':analytics_actor}


class Gateway:
    def __init__(self,w,broken=None,lost=None,wrong=None,asynchronous=False):
        self.w,self.broken,self.lost,self.wrong,self.asynchronous=w,broken,lost,wrong,asynchronous
    def deliver(self,message):
        assert self.w['db'].engine.pool.checkedout()==0
        destination=message['destination']
        if destination==self.broken:raise ApplicationError(503,'Participant unavailable')
        participant=self.w[destination]
        if destination=='analytics' and self.asynchronous:participant=participant.api
        try:ack=participant.receive(message['id'],message['event_name'],message['schema_version'],message['payload'])
        except Exception as error:raise ApplicationError(getattr(error,'status_code',503),'Participant failed') from None
        if destination==self.lost:raise ApplicationError(503,'Acknowledgement lost after commit')
        receipt=ack.get('receipt')
        if destination==self.wrong:
            receipt={**receipt,**({'points':receipt['points']+1} if destination=='rating' else {'payloadHash':'0'*64})}
        return receipt


def drain(w,**options):
    worker=DeliverRewards(w['service'].uow,Gateway(w,**options))
    for _ in range(15):
        if not worker.once():return worker
    raise AssertionError('Delivery did not quiesce')


def ready(w):
    with w['db'].transaction() as session:
        for row in session.scalars(select(OutboxMessage).where(OutboxMessage.published_at.is_(None))):
            row.available_at=datetime.now(timezone.utc)-timedelta(seconds=1)


def begin(w,**changes):
    reply=start(w['local']);w['clock'].advance(60)
    return reply,finish(w['local'],reply,**changes)


def state(w,reply):return w['service'].get(w['account'],UUID(reply['runId']))


def score(w):
    entries=w['rating'].ranking('run-xp')['entries']
    return entries[0]['score'] if entries else None


def source(w,run_id,destination):
    with w['db'].transaction() as session:
        return session.scalar(select(OutboxMessage).where(OutboxMessage.aggregate_id==UUID(run_id),OutboxMessage.destination==destination))


def test_local_success_uses_xp_delta_and_mode_aware_stable_events(world):
    w=world;reply,initial=begin(w);run=UUID(reply['runId'])
    assert initial['ratingStatus']==initial['analyticsStatus']=='PENDING'
    assert initial['rating']['points']==30 and initial['rating']['boardCode']=='run-xp'
    rating,analytics=(source(w,reply['runId'],d) for d in ('rating','analytics'))
    assert rating.id==rating_message_id(run) and analytics.id==analytics_message_id(run)
    assert analytics.payload['simulationMode']=='LOCAL'
    assert 'sessionId' not in analytics.payload and 'displayName' not in analytics.payload
    drain(w)
    final=state(w,reply)
    assert final['ratingStatus']==final['analyticsStatus']==final['rewardStatus']=='COMPLETED'
    assert final['rating']['receipt']['scoreAfter']==score(w)==30
    assert final['analytics']['receipt']==expected_receipt(analytics.payload)
    tables=w['analytics'].projection.tables
    assert len(tables['raw_game_events'])==3
    assert tables['fact_run_settlements'][run]['simulation_mode']=='LOCAL'
    assert next(iter(tables['fact_xp_transactions'].values()))['source_type']=='LOCAL_RUN'
    assert next(iter(tables['fact_player_level_ups'].values()))['new_level']==2
    assert w['client'].get('/v1/local-runs/'+str(run),headers=AUTH).json()['analytics']==final['analytics']
    assert w['client'].get('/v1/runs/'+str(run),headers=AUTH).json()['rating']==final['rating']


def test_concurrent_finish_and_new_transport_ids_cannot_repeat_points_or_events(world):
    w=world;reply=start(w['local']);w['clock'].advance(60)
    with ThreadPoolExecutor(max_workers=5) as pool:
        receipts=list(pool.map(lambda _:finish(w['local'],reply),range(5)))
    assert sum(not r['replayed'] for r in receipts)==1
    drain(w)
    for destination in ('rating','analytics'):
        message=source(w,reply['runId'],destination)
        with ThreadPoolExecutor(max_workers=4) as pool:
            acknowledgements=list(pool.map(lambda _:w[destination].receive(uuid4(),message.event_name,1,message.payload),range(4)))
        assert all(a['receipt']==acknowledgements[0]['receipt'] for a in acknowledgements)
    assert score(w)==30 and w['analytics'].projection.calls==1
    with w['db'].transaction() as session:
        for model in (PlayerXPTransaction,RunEnergyCharge,RunResultSubmission,RunValidationAudit):
            assert session.scalar(select(func.count()).select_from(model))==1
        assert session.scalar(select(func.count()).select_from(OutboxMessage))==4
        assert session.get(Profile,w['account']).version==2
    with pytest.raises(ApplicationError,match='payload changed'):finish(w['local'],reply,churchLevel=4)


@pytest.mark.parametrize('destination',['rating','analytics'])
@pytest.mark.parametrize('failure',['down','lost','wrong'])
def test_independent_delivery_recovers_without_repeating_rewards(world,destination,failure):
    w=world;reply,initial=begin(w)
    drain(w,**{{'down':'broken','lost':'lost','wrong':'wrong'}[failure]:destination})
    partial=state(w,reply)
    assert partial[destination+'Status']=='PENDING' and partial['rewardStatus']=='COMPLETED'
    other='analytics' if destination=='rating' else 'rating'
    assert partial[other+'Status']=='COMPLETED'
    money=w['wallet'].balance(w['account']);items=w['inventory'].get(w['account'])
    ready(w);drain(w)
    complete=finish(w['local'],reply)
    assert complete['ratingStatus']==complete['analyticsStatus']=='COMPLETED'
    assert complete['settlement']==initial['settlement'] and score(w)==30
    assert w['wallet'].balance(w['account'])==money and w['inventory'].get(w['account'])==items
    assert w['analytics'].projection.calls==1


@pytest.mark.parametrize('destination',['rating','analytics'])
def test_review_and_manual_replay_keep_same_source_and_board(world,destination):
    w=world;reply,_=begin(w)
    original=source(w,reply['runId'],destination)
    with w['db'].transaction() as session:session.get(OutboxMessage,original.id).attempts=19
    worker=drain(w,broken=destination)
    assert state(w,reply)[destination+'Status']=='REVIEW'
    assert state(w,reply)['rewardStatus']=='COMPLETED'
    w['service'].settings=replace(w['service'].settings,rating_board_code='a-new-board')
    assert worker.requeue(original.id)
    drain(w)
    complete=state(w,reply)
    assert complete['ratingStatus']==complete['analyticsStatus']=='COMPLETED' and score(w)==30
    current=source(w,reply['runId'],destination)
    assert current.id==original.id and current.payload==original.payload
    assert finish(w['local'],reply)['rating']['boardCode']=='run-xp'


@pytest.mark.parametrize('destination',['rating','analytics'])
def test_local_confirmation_db_failure_retries_already_committed_remote_result(world,destination):
    w=world;reply,_=begin(w);message=source(w,reply['runId'],destination)
    with w['db'].engine.begin() as conn:
        conn.exec_driver_sql(f"ALTER TABLE outbox_messages ADD CONSTRAINT test_local_confirm CHECK (id <> '{message.id}'::uuid OR published_at IS NULL)")
    try:
        with pytest.raises(IntegrityError):drain(w)
        assert state(w,reply)[destination+'Status']=='PENDING'
    finally:
        with w['db'].engine.begin() as conn:conn.execute(text('ALTER TABLE outbox_messages DROP CONSTRAINT test_local_confirm'))
    ready(w);drain(w)
    assert state(w,reply)[destination+'Status']=='COMPLETED' and score(w)==30
    assert w['analytics'].projection.calls==1


def test_async_analytics_and_projection_failure_require_final_ack(world,monkeypatch):
    w=world;reply,_=begin(w);run=UUID(reply['runId'])
    drain(w,asynchronous=True)
    assert state(w,reply)['analyticsStatus']=='PENDING' and state(w,reply)['ratingStatus']=='COMPLETED'
    actor=w['analytics'];project=actor.projection.project
    def lost(payload):
        project(payload)
        raise RuntimeError('Warehouse response lost after writes')
    monkeypatch.setattr(actor.projection,'project',lost)
    with pytest.raises(RuntimeError):actor.worker.once()
    assert actor.api.get(run)['status']=='PENDING'
    with actor.database.transaction() as session:
        session.get(AnalyticsInbox,('run.analytics',run)).available_at=datetime.now(timezone.utc)-timedelta(seconds=1)
    monkeypatch.setattr(actor.projection,'project',project)
    assert actor.worker.once()
    ready(w);drain(w)
    assert state(w,reply)['analyticsStatus']=='COMPLETED'
    assert len(actor.projection.tables['raw_game_events'])==3
    assert len(actor.projection.tables['fact_run_settlements'])==len(actor.projection.tables['fact_xp_transactions'])==1
    assert score(w)==30


def test_settlement_failure_creates_neither_delivery_then_worker_restores_both(world,monkeypatch):
    from vatican_backend.persistence.postgres.repositories.runs import RunsRepository
    w=world;reply=start(w['local']);w['clock'].advance(60)
    record=RunsRepository.record_result
    def fail(*args,**kwargs):raise RuntimeError('Settlement interrupted')
    monkeypatch.setattr(RunsRepository,'record_result',fail)
    with pytest.raises(RuntimeError):finish(w['local'],reply)
    assert state(w,reply)['settlementStatus']=='PENDING'
    with w['db'].transaction() as session:
        assert session.scalar(select(func.count()).select_from(OutboxMessage))==0
        assert session.scalar(select(func.count()).select_from(PlayerXPTransaction))==0
    monkeypatch.setattr(RunsRepository,'record_result',record)
    w['clock'].advance(86400)
    assert w['service'].maintain()==1
    drain(w)
    assert state(w,reply)['ratingStatus']==state(w,reply)['analyticsStatus']=='COMPLETED'
    assert source(w,reply['runId'],'analytics').payload['simulationMode']=='LOCAL'
    assert score(w)==30


@pytest.mark.parametrize('hero_kills,xp',[(0,0),(2,10)])
def test_defeat_and_zero_points_are_deduplicated_contributions(world,hero_kills,xp):
    w=world;reply,settled=begin(w,outcome='defeat',churchLevel=1,
        stats={'heroKills':{'orc':hero_kills},'towerKills':{},'buildingUpgrades':{}})
    drain(w)
    assert score(w)==xp and settled['rating']['points']==xp
    assert len(w['analytics'].projection.tables['raw_game_events'])==2
    assert settled['settlement']['permanentReward']==0
    assert finish(w['local'],reply,outcome='defeat',churchLevel=1,
        stats={'heroKills':{'orc':hero_kills},'towerKills':{},'buildingUpgrades':{}})['rating']['receipt']['points']==xp


def test_mode_is_part_of_immutable_analytics_source(world):
    from vatican_analytics.application.errors import ApplicationError as AnalyticsError
    w=world;reply,_=begin(w);drain(w)
    message=source(w,reply['runId'],'analytics')
    with pytest.raises(AnalyticsError) as error:
        w['analytics'].receive(uuid4(),message.event_name,1,{**message.payload,'simulationMode':'DEDICATED'})
    assert error.value.status_code==409 and w['analytics'].projection.calls==1


def test_historical_completed_local_is_not_backfilled_on_replay(world):
    from sqlalchemy import delete
    w=world;reply,old=begin(w)
    with w['db'].transaction() as session:
        session.execute(delete(OutboxMessage).where(OutboxMessage.destination.in_(('rating','analytics'))))
    replay=finish(w['local'],reply)
    assert replay['replayed'] and replay['settlement']==old['settlement']
    assert replay['ratingStatus']==replay['analyticsStatus']=='SKIPPED'
    with w['db'].transaction() as session:assert session.scalar(select(func.count()).select_from(OutboxMessage))==2


def test_second_local_contributes_only_its_xp_delta_and_old_receipt_stays_frozen(world):
    w=world;first,initial=begin(w);drain(w)
    second,settled=begin(w,outcome='defeat',churchLevel=1)
    assert settled['settlement']['progression']['totalXp']==40
    assert settled['rating']['points']==10
    drain(w)
    assert score(w)==40
    assert finish(w['local'],first)['rating']['receipt']['scoreAfter']==30
    assert finish(w['local'],first)['settlement']==initial['settlement']
    assert len(w['analytics'].projection.tables['fact_run_settlements'])==2


def test_rating_receipt_commit_failure_rolls_back_score_and_recovers(world):
    w=world;reply,_=begin(w);run=UUID(reply['runId'])
    with w['rating_db'].engine.begin() as conn:
        conn.exec_driver_sql(f"ALTER TABLE run_score_receipts ADD CONSTRAINT test_local_rating CHECK (run_id <> '{run}'::uuid)")
    try:
        drain(w)
        assert score(w) is None and state(w,reply)['ratingStatus']=='PENDING'
        assert state(w,reply)['analyticsStatus']==state(w,reply)['rewardStatus']=='COMPLETED'
        with w['rating_db'].transaction() as session:assert session.get(RunScoreReceipt,run) is None
    finally:
        with w['rating_db'].engine.begin() as conn:conn.execute(text('ALTER TABLE run_score_receipts DROP CONSTRAINT test_local_rating'))
    ready(w);drain(w)
    assert score(w)==30 and state(w,reply)['ratingStatus']=='COMPLETED'


def test_analytics_projection_review_can_resume_without_repeating_other_consumers(world,monkeypatch):
    w=world;reply,_=begin(w);run=UUID(reply['runId'])
    drain(w,asynchronous=True)
    actor=w['analytics'];project=actor.projection.project
    with actor.database.transaction() as session:session.get(AnalyticsInbox,('run.analytics',run)).attempts=19
    def fail(payload):raise RuntimeError('Warehouse unavailable')
    monkeypatch.setattr(actor.projection,'project',fail)
    with pytest.raises(RuntimeError):actor.worker.once()
    assert actor.api.get(run)['status']=='REVIEW'
    ready(w);drain(w)
    assert state(w,reply)['analyticsStatus']=='PENDING' and state(w,reply)['ratingStatus']=='COMPLETED'
    monkeypatch.setattr(actor.projection,'project',project)
    assert actor.worker.requeue(run)
    assert actor.worker.once()
    ready(w);drain(w)
    assert state(w,reply)['analyticsStatus']=='COMPLETED' and score(w)==30
    assert len(actor.projection.tables['raw_game_events'])==3
