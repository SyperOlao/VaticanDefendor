from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone
from uuid import UUID, uuid4
import pytest
from sqlalchemy import func, select, text
from sqlalchemy.exc import IntegrityError
from vatican_backend.application.errors import ApplicationError
from vatican_backend.application.messaging import DeliverRewards
from vatican_backend.domain.analytics import ANALYTICS_EVENT, analytics_message_id, expected_receipt
from vatican_backend.persistence.postgres.tables import OutboxMessage
from vatican_analytics.persistence.postgres.tables import AnalyticsInbox
from .test_level_rewards_lifecycle import gift_game, ready_again, balances
from .test_progression_lifecycle import xp_game, start, apply


@pytest.fixture
def analytics_world(gift_game,xp_game):
    w=gift_game;w['xp_game']=xp_game;w['result']=start(xp_game);w['reply']=apply(xp_game,w['result'])
    return w


def state(w):return w['client'].app.state.use_cases.runs.assignment_status(w['result'].run_id,w['account'])


class Delivery:
    def __init__(self,w,down=False,lost=False,wrong=False,asynchronous=False):
        self.w,self.down,self.lost,self.wrong,self.asynchronous=w,down,lost,wrong,asynchronous
    def deliver(self,message):
        assert getattr(self.w['active'],'game',0)==0
        analytics=message['destination']=='analytics'
        if analytics and self.down:raise ApplicationError(503,'Analytics unavailable')
        target=self.w[message['destination']]
        if analytics and self.asynchronous:target=target.api
        reply=target.receive(message['id'],message['event_name'],message['schema_version'],message['payload'])
        if reply['status']=='PENDING':raise ApplicationError(503,'Analytics projection is pending')
        if reply['status']=='REVIEW':raise ApplicationError(422,'Analytics projection requires review')
        if analytics and self.lost:raise ApplicationError(503,'Analytics acknowledgement lost')
        receipt=reply.get('receipt')
        if analytics and self.wrong:receipt={**receipt,'payloadHash':'0'*64}
        return receipt


def worker(w,**options):return DeliverRewards(w['use_case'].uow,Delivery(w,**options))


def drain(w,**options):
    service=worker(w,**options)
    for _ in range(25):
        if not service.once():return
    raise AssertionError('Delivery did not quiesce')


def test_outbox_payload_is_server_owned_allowlist_and_receipt_matches_all_events(analytics_world):
    w=analytics_world
    assert w['reply']['analyticsStatus']=='PENDING'
    with w['game'].transaction() as session:
        row=session.get(OutboxMessage,analytics_message_id(w['result'].run_id))
        payload=row.payload
        assert set(payload)=={'accountId','runId','rulesetId','contentReleaseId','completedAt','outcome','churchLevel','stats','progression'}
        assert set(payload['progression'])=={'xpGranted','totalXp','previousLevel','currentLevel','breakdown'}
    drain(w)
    final=state(w)
    assert final['analyticsStatus']=='COMPLETED' and final['analytics']['receipt']==expected_receipt(payload)
    assert len(final['analytics']['receipt']['eventIds'])==3
    assert apply(w['xp_game'],w['result'])['analytics']==final['analytics']


def test_offline_analytics_does_not_block_rewards_rating_or_xp_and_resumes(analytics_world):
    w=analytics_world;drain(w,down=True)
    pending=state(w)
    assert pending['analyticsStatus']=='PENDING' and pending['ratingStatus']=='COMPLETED' and pending['rewardStatus']=='COMPLETED'
    assert pending['progression']['totalXp']==160
    money,weapons=balances(w),w['inventory'].get(w['account'])
    ready_again(w);drain(w)
    assert state(w)['analyticsStatus']=='COMPLETED'
    assert balances(w)==money and w['inventory'].get(w['account'])==weapons


def test_async_inbox_acceptance_is_not_game_confirmation_until_projection_finishes(analytics_world):
    w=analytics_world;drain(w,asynchronous=True)
    assert state(w)['analyticsStatus']=='PENDING'
    assert w['analytics'].api.get(w['result'].run_id)['status']=='PENDING'
    assert w['analytics'].worker.once()
    ready_again(w);drain(w,asynchronous=True)
    assert state(w)['analyticsStatus']=='COMPLETED'


def test_result_concurrency_and_message_aliases_do_not_repeat_event_processing(analytics_world):
    w=analytics_world
    with ThreadPoolExecutor(max_workers=6) as pool:
        replies=list(pool.map(lambda _:apply(w['xp_game'],w['result']),range(6)))
    assert all(r['replayed'] for r in replies)
    drain(w,lost=True)
    assert state(w)['analyticsStatus']=='PENDING'
    calls=w['analytics'].projection.calls
    ready_again(w);drain(w)
    assert state(w)['analyticsStatus']=='COMPLETED' and w['analytics'].projection.calls==calls
    with w['game'].transaction() as session:payload=session.get(OutboxMessage,analytics_message_id(w['result'].run_id)).payload
    alias=w['analytics'].receive(uuid4(),ANALYTICS_EVENT,1,payload)
    assert alias['receipt']==state(w)['analytics']['receipt'] and w['analytics'].projection.calls==calls
    assert len(w['analytics'].projection.tables['fact_run_settlements'])==2


def test_wrong_ack_is_retried_without_reprocessing_already_projected_source(analytics_world):
    w=analytics_world;drain(w,wrong=True)
    assert state(w)['analyticsStatus']=='PENDING'
    calls=w['analytics'].projection.calls
    ready_again(w);drain(w)
    assert state(w)['analyticsStatus']=='COMPLETED' and w['analytics'].projection.calls==calls


def test_delivery_exhaustion_and_analytics_review_can_be_replayed_without_payload_changes(analytics_world):
    w=analytics_world
    with w['game'].transaction() as session:session.get(OutboxMessage,analytics_message_id(w['result'].run_id)).attempts=19
    drain(w,down=True)
    assert state(w)['analyticsStatus']=='REVIEW' and state(w)['rewardStatus']=='COMPLETED'
    identity=UUID(state(w)['analytics']['messageId'])
    assert w['client'].app.state.use_cases.rewards.requeue(identity)
    drain(w)
    assert state(w)['analyticsStatus']=='COMPLETED'


def test_game_confirm_commit_failure_does_not_repeat_clickhouse_projection(analytics_world):
    w=analytics_world;identity=analytics_message_id(w['result'].run_id)
    with w['game'].engine.begin() as conn:
        conn.exec_driver_sql(f"ALTER TABLE outbox_messages ADD CONSTRAINT injected_analytics_confirm CHECK (id <> '{identity}'::uuid OR published_at IS NULL)")
    try:
        with pytest.raises(IntegrityError):drain(w)
        assert state(w)['analyticsStatus']=='PENDING'
        calls=w['analytics'].projection.calls
    finally:
        with w['game'].engine.begin() as conn:conn.execute(text('ALTER TABLE outbox_messages DROP CONSTRAINT injected_analytics_confirm'))
    ready_again(w);drain(w)
    assert state(w)['analyticsStatus']=='COMPLETED' and w['analytics'].projection.calls==calls


def test_stale_game_lease_cannot_confirm_analytics_delivery(analytics_world):
    w=analytics_world;identity=analytics_message_id(w['result'].run_id)
    with w['game'].transaction() as session:
        for row in session.scalars(select(OutboxMessage).where(OutboxMessage.aggregate_id==w['result'].run_id)):
            if row.id!=identity:row.available_at=datetime.now(timezone.utc)+timedelta(minutes=5)
    with w['use_case'].uow() as work:first=work.messages.claim()
    receipt=Delivery(w).deliver(first)
    with w['game'].transaction() as session:session.get(OutboxMessage,identity).available_at=datetime.now(timezone.utc)-timedelta(seconds=1)
    with w['use_case'].uow() as work:
        newer=work.messages.claim()
        assert not work.messages.confirm(first['id'],first['lease'],receipt)
        assert work.messages.confirm(newer['id'],newer['lease'],receipt)
    assert state(w)['analyticsStatus']=='COMPLETED'


def test_result_failure_rolls_back_analytics_outbox_with_xp(gift_game,xp_game,monkeypatch):
    from vatican_backend.persistence.postgres.repositories.runs import RunsRepository
    w=gift_game;result=start(xp_game);original=RunsRepository.record_result
    def fail(*args,**kwargs):raise RuntimeError('Injected result failure')
    monkeypatch.setattr(RunsRepository,'record_result',fail)
    with pytest.raises(RuntimeError):apply(xp_game,result)
    with w['game'].transaction() as session:assert session.get(OutboxMessage,analytics_message_id(result.run_id)) is None
    monkeypatch.setattr(RunsRepository,'record_result',original)
    assert apply(xp_game,result)['analyticsStatus']=='PENDING'


def test_owned_run_http_poll_exposes_delivery_but_not_internal_payload(analytics_world):
    w=analytics_world;endpoint='/v1/runs/'+str(w['result'].run_id)
    assert w['client'].get(endpoint).status_code==401
    headers={'Authorization':'Bearer test-token'}
    result=w['client'].get(endpoint,headers=headers).json()
    assert result['analyticsStatus']=='PENDING' and 'payload' not in result['analytics']
    drain(w)
    assert w['client'].get(endpoint,headers=headers).json()['analyticsStatus']=='COMPLETED'


def test_environment_keeps_rating_board_validation_and_analytics_endpoint_independent(monkeypatch):
    from vatican_backend.bootstrap.settings import Settings
    for key,value in {'VD_DATABASE_URL':'postgresql://localhost/test','VD_SERVER_POOL_JSON':'[]',
            'VD_RUN_REWARDS_JSON':'{}','VD_RATING_BOARD_CODE':'run-xp',
            'VD_SERVICE_SECRET':'s'*32,
            'VD_ANALYTICS_URL':'http://analytics:8080'}.items():
        monkeypatch.setenv(key,value)
    settings=Settings.from_environment()
    assert settings.analytics_url=='http://analytics:8080' and settings.rating_board_code=='run-xp'
    monkeypatch.setenv('VD_RATING_BOARD_CODE','invalid/name')
    with pytest.raises(ValueError,match='VD_RATING_BOARD_CODE'):Settings.from_environment()
