from concurrent.futures import ThreadPoolExecutor
from dataclasses import replace
from datetime import datetime, timedelta, timezone
from uuid import UUID, uuid4
import pytest
from sqlalchemy import func, select, text
from sqlalchemy.exc import IntegrityError
from vatican_backend.application.errors import ApplicationError
from vatican_backend.application.messaging import DeliverRewards
from vatican_backend.domain.rating import RATING_EVENT, rating_message_id
from vatican_backend.domain.progression import RunStats
from vatican_backend.persistence.postgres.tables import OutboxMessage, PlayerXPTransaction
from vatican_rating.application.errors import ApplicationError as RatingError
from vatican_rating.persistence.postgres.tables import Leaderboard, LeaderboardScore, RunScoreReceipt, ConsumedMessage
from .test_level_rewards_lifecycle import gift_game, ready_again, balances
from .test_progression_lifecycle import xp_game, start, apply


@pytest.fixture
def rating_world(gift_game,xp_game):
    w=gift_game
    w['xp_game']=xp_game
    w['result']=start(xp_game)
    w['reply']=apply(xp_game,w['result'])
    return w


def status(w):
    return w['client'].app.state.use_cases.runs.assignment_status(w['result'].run_id,w['account'])


def score(w):
    return next(row['score'] for row in w['rating'].ranking('run-xp')['entries'] if row['accountId']==str(w['account']))


class Delivery:
    def __init__(self,w,down=False,lost=False,wrong=False,rejected=False):
        self.w,self.down,self.lost,self.wrong,self.rejected=w,down,lost,wrong,rejected
    def deliver(self,message):
        assert getattr(self.w['active'],'game',0)==0
        rating=message['destination']=='rating'
        if rating and self.down:raise ApplicationError(503,'Rating unavailable')
        if rating and self.rejected:raise ApplicationError(422,'Rating rejected message')
        participant=self.w[message['destination']]
        reply=participant.receive(message['id'],message['event_name'],message['schema_version'],message['payload'])
        if rating and self.lost:raise ApplicationError(503,'Rating acknowledgement lost')
        receipt=reply.get('receipt')
        return {**receipt,'points':receipt['points']+1} if rating and self.wrong else receipt


def worker(w,**options):
    return DeliverRewards(w['use_case'].uow,Delivery(w,**options))


def drain(w,**options):
    service=worker(w,**options)
    for _ in range(25):
        if not service.once():return
    raise AssertionError('Delivery did not quiesce')


def test_run_outbox_uses_run_xp_delta_and_persists_matching_receipt(rating_world):
    w=rating_world
    reply=w['reply']
    assert reply['ratingStatus']=='PENDING' and reply['rating']['points']==80
    assert reply['progression']['totalXp']==160
    assert UUID(reply['rating']['messageId'])==rating_message_id(w['result'].run_id)
    drain(w)
    result=status(w)
    assert result['ratingStatus']=='COMPLETED' and result['rewardStatus']=='COMPLETED'
    assert score(w)==160
    assert result['rating']['receipt']['points']==80 and result['rating']['receipt']['scoreAfter']==160
    with w['game'].transaction() as session:
        row=session.get(OutboxMessage,rating_message_id(w['result'].run_id))
        assert row.receipt_json==result['rating']['receipt'] and row.published_at
    assert apply(w['xp_game'],w['result'])['rating']==result['rating']


def test_concurrent_result_replay_creates_one_rating_source_and_rejects_changed_result(rating_world):
    w=rating_world
    with ThreadPoolExecutor(max_workers=6) as pool:
        replies=list(pool.map(lambda _:apply(w['xp_game'],w['result']),range(6)))
    assert all(r['replayed'] and r['rating']['points']==80 for r in replies)
    with pytest.raises(ApplicationError,match='Conflicting result'):
        apply(w['xp_game'],replace(w['result'],stats=RunStats((('orc',5),))))
    drain(w)
    assert score(w)==160
    with w['rating_db'].transaction() as session:
        assert session.scalar(select(func.count()).select_from(RunScoreReceipt))==2


def test_rating_outage_does_not_block_money_items_xp_and_recovers_automatically(rating_world):
    w=rating_world
    drain(w,down=True)
    pending=status(w)
    assert pending['ratingStatus']=='PENDING' and pending['rewardStatus']=='COMPLETED'
    assert pending['progression']['totalXp']==160 and score(w)==80
    before_money,before_weapons=balances(w),w['inventory'].get(w['account'])
    ready_again(w);drain(w)
    assert status(w)['ratingStatus']=='COMPLETED' and score(w)==160
    assert balances(w)==before_money and w['inventory'].get(w['account'])==before_weapons


def test_lost_rating_ack_and_changed_message_id_do_not_repeat_points(rating_world):
    w=rating_world
    drain(w,lost=True)
    assert score(w)==160 and status(w)['ratingStatus']=='PENDING'
    with w['game'].transaction() as session:
        row=session.get(OutboxMessage,rating_message_id(w['result'].run_id))
        payload=row.payload
    receipt=w['rating'].receive(uuid4(),RATING_EVENT,1,payload)['receipt']
    assert receipt['scoreAfter']==160
    ready_again(w);drain(w)
    assert score(w)==160 and status(w)['rating']['receipt']==receipt


def test_source_owner_board_amount_and_rules_cannot_be_changed_with_a_new_message_id(rating_world):
    w=rating_world;drain(w)
    with w['game'].transaction() as session:payload=session.get(OutboxMessage,rating_message_id(w['result'].run_id)).payload
    for patch in ({'accountId':str(uuid4())},{'points':81},{'boardCode':'other'},
                  {'rulesetId':'other-v2'},{'contentReleaseId':'other-content'}):
        with pytest.raises(RatingError) as error:w['rating'].receive(uuid4(),RATING_EVENT,1,{**payload,**patch})
        assert error.value.status_code==409
    assert score(w)==160


@pytest.mark.parametrize('permanent',[False,True])
def test_review_and_manual_replay_preserve_the_rating_source_and_other_rewards(rating_world,permanent):
    w=rating_world
    if not permanent:
        with w['game'].transaction() as session:
            session.get(OutboxMessage,rating_message_id(w['result'].run_id)).attempts=19
    drain(w,down=not permanent,rejected=permanent)
    failed=status(w)
    assert failed['ratingStatus']=='REVIEW' and failed['rewardStatus']=='COMPLETED'
    money=balances(w)
    assert w['client'].app.state.use_cases.rewards.requeue(UUID(failed['rating']['messageId']))
    drain(w)
    assert score(w)==160 and status(w)['ratingStatus']=='COMPLETED' and balances(w)==money


def test_bad_receipt_is_retried_instead_of_confirming_another_contribution(rating_world):
    w=rating_world;drain(w,wrong=True)
    assert status(w)['ratingStatus']=='PENDING' and score(w)==160
    ready_again(w);drain(w)
    assert status(w)['ratingStatus']=='COMPLETED' and score(w)==160


def test_game_confirmation_commit_failure_replays_the_already_applied_rating(rating_world):
    w=rating_world
    with w['game'].engine.begin() as conn:
        conn.exec_driver_sql(f"ALTER TABLE outbox_messages ADD CONSTRAINT injected_rating_confirmation CHECK (id <> '{rating_message_id(w['result'].run_id)}'::uuid OR published_at IS NULL)")
    try:
        with pytest.raises(IntegrityError):drain(w)
        assert score(w)==160 and status(w)['ratingStatus']=='PENDING'
    finally:
        with w['game'].engine.begin() as conn:conn.execute(text('ALTER TABLE outbox_messages DROP CONSTRAINT injected_rating_confirmation'))
    ready_again(w);drain(w)
    assert status(w)['ratingStatus']=='COMPLETED' and score(w)==160


def test_rating_commit_failure_rolls_back_score_receipt_and_message_together(rating_world):
    w=rating_world
    with w['rating_db'].engine.begin() as conn:
        conn.exec_driver_sql(f"ALTER TABLE run_score_receipts ADD CONSTRAINT injected_rating_receipt CHECK (run_id <> '{w['result'].run_id}'::uuid)")
    try:
        with pytest.raises(IntegrityError):drain(w)
        assert score(w)==80
        with w['rating_db'].transaction() as session:
            assert session.get(RunScoreReceipt,w['result'].run_id) is None
            assert session.get(ConsumedMessage,('rewards.v1',rating_message_id(w['result'].run_id))) is None
    finally:
        with w['rating_db'].engine.begin() as conn:conn.execute(text('ALTER TABLE run_score_receipts DROP CONSTRAINT injected_rating_receipt'))
    ready_again(w);drain(w)
    assert score(w)==160 and status(w)['ratingStatus']=='COMPLETED'


def test_parallel_workers_and_stale_leases_cannot_double_score_or_confirm_wrong_lease(rating_world):
    w=rating_world
    # Select the rating job first for this lease test; other rewards stay pending.
    with w['game'].transaction() as session:
        for row in session.scalars(select(OutboxMessage).where(OutboxMessage.aggregate_id==w['result'].run_id)):
            if row.destination!='rating':row.available_at=datetime.now(timezone.utc)+timedelta(minutes=5)
    with w['use_case'].uow() as work:first=work.messages.claim()
    receipt=Delivery(w).deliver(first)
    with w['game'].transaction() as session:
        session.get(OutboxMessage,first['id']).available_at=datetime.now(timezone.utc)-timedelta(seconds=1)
    with w['use_case'].uow() as work:
        row=work.messages.claim()
        assert row['id']==first['id']
        assert not work.messages.confirm(first['id'],first['lease'],receipt)
    assert status(w)['ratingStatus']=='PENDING'
    ready_again(w)
    with ThreadPoolExecutor(max_workers=4) as pool:
        for _ in range(5):list(pool.map(lambda _:worker(w).once(),range(4)))
    assert status(w)['ratingStatus']=='COMPLETED' and score(w)==160


def test_board_route_and_points_stay_frozen_across_settings_changes(rating_world):
    w=rating_world
    service=w['client'].app.state.use_cases.runs
    service.settings=replace(service.settings,rating_board_code='next-season')
    assert apply(w['xp_game'],w['result'])['rating']['boardCode']=='run-xp'
    drain(w)
    assert score(w)==160 and status(w)['rating']['points']==80


def test_result_commit_failure_rolls_back_rating_outbox_and_xp_before_retry(gift_game,xp_game,monkeypatch):
    from vatican_backend.persistence.postgres.repositories.runs import RunsRepository
    w=gift_game;result=start(xp_game)
    original=RunsRepository.record_result
    def fail(*args,**kwargs):raise RuntimeError('Injected result commit failure')
    monkeypatch.setattr(RunsRepository,'record_result',fail)
    with pytest.raises(RuntimeError):apply(xp_game,result)
    with w['game'].transaction() as session:
        assert session.get(OutboxMessage,rating_message_id(result.run_id)) is None
        assert session.scalar(select(func.count()).select_from(PlayerXPTransaction))==1
    monkeypatch.setattr(RunsRepository,'record_result',original)
    reply=apply(xp_game,result)
    assert reply['ratingStatus']=='PENDING' and reply['progression']['xpGranted']==80


def test_zero_xp_run_is_recorded_once_and_legacy_unpinned_run_is_skipped(gift_game,xp_game):
    w=gift_game
    zero=start(xp_game,RunStats(),outcome='defeat')
    reply=apply(xp_game,zero)
    assert reply['rating']['points']==0 and reply['progression']['xpGranted']==0
    w['result']=zero;drain(w)
    assert score(w)==80 and status(w)['ratingStatus']=='COMPLETED'
    with w['rating_db'].transaction() as session:
        assert session.get(RunScoreReceipt,zero.run_id).points==0
    legacy=start(xp_game,RunStats(),outcome='defeat')
    from vatican_backend.persistence.postgres.tables import Run
    with w['game'].transaction() as session:session.get(Run,legacy.run_id).xp_rules_snapshot=None
    skipped=apply(xp_game,legacy)
    assert skipped['progression'] is None and skipped['ratingStatus']=='SKIPPED'
    assert apply(xp_game,legacy)['ratingStatus']=='SKIPPED'
    w['result']=legacy;drain(w)
    assert score(w)==80 and status(w)['ratingStatus']=='SKIPPED'


def test_http_run_poll_exposes_own_rating_delivery_and_not_another_players(rating_world):
    w=rating_world
    endpoint='/v1/runs/'+str(w['result'].run_id)
    assert w['client'].get(endpoint).status_code==401
    headers={'Authorization':'Bearer test-token'}
    assert w['client'].get(endpoint,headers=headers).json()['ratingStatus']=='PENDING'
    with pytest.raises(ApplicationError) as error:
        w['client'].app.state.use_cases.runs.assignment_status(w['result'].run_id,uuid4())
    assert error.value.status_code==404
    drain(w)
    assert w['client'].get(endpoint,headers=headers).json()['ratingStatus']=='COMPLETED'


@pytest.mark.parametrize('fault', ['lock', '40001', '40P01', '57014', '408'])
def test_http_database_failure_stays_pending_then_recovers_without_manual_replay(rating_world, fault):
    from contextlib import nullcontext
    from fastapi.testclient import TestClient
    from vatican_backend.integrations.http import HttpRewardDelivery
    from vatican_rating.bootstrap.settings import Settings as RatingSettings
    from vatican_rating.presentation.app import create_app as rating_app

    w = rating_world
    secret = 's' * 40
    injected_trigger = fault not in ('lock', '408')
    if injected_trigger:
        with w['rating_db'].engine.begin() as connection:
            connection.execute(text(f"""CREATE FUNCTION injected_rating_failure() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION USING ERRCODE = '{fault}', MESSAGE = 'Injected transient failure'; END $$"""))
            connection.execute(text('CREATE TRIGGER injected_failure BEFORE INSERT ON run_score_receipts '
                                    'FOR EACH ROW EXECUTE FUNCTION injected_rating_failure()'))

    with TestClient(rating_app(RatingSettings(w['rating_db'].engine.url.render_as_string(hide_password=False), secret,
                                             lock_timeout_ms=80, statement_timeout_ms=1000))) as client:
        class Sender:
            timed_out = False

            def post(self, *args, **kwargs):
                if fault == '408' and not self.timed_out:
                    import httpx
                    self.timed_out = True
                    return httpx.Response(408)
                return client.post(*args, **kwargs)

        http_delivery = HttpRewardDelivery(Sender(), {'rating': 'http://testserver'}, secret)

        class DeliveryViaHttp(Delivery):
            def deliver(self, message):
                if message['destination'] == 'rating':
                    return http_delivery.deliver(message)
                return super().deliver(message)

        service = DeliverRewards(w['use_case'].uow, DeliveryViaHttp(w))

        def deliver_ready():
            for _ in range(25):
                if not service.once():
                    return
            raise AssertionError('Delivery did not quiesce')

        blocker = w['rating_db'].transaction() if fault == 'lock' else nullcontext()
        with blocker as session:
            if fault == 'lock':
                # Service fixtures share one physical database, so account advisory
                # locks would also block wallet/inventory. Lock only rating's row.
                session.scalar(select(LeaderboardScore).where(LeaderboardScore.account_id == w['account'])
                               .with_for_update())
            deliver_ready()
            pending = status(w)
            assert pending['ratingStatus'] == 'PENDING'
            assert pending['rewardStatus'] == 'COMPLETED'
            assert score(w) == 80
        if injected_trigger:
            with w['rating_db'].engine.begin() as connection:
                connection.execute(text('DROP TRIGGER injected_failure ON run_score_receipts'))
        ready_again(w)
        deliver_ready()
        assert status(w)['ratingStatus'] == 'COMPLETED' and score(w) == 160
