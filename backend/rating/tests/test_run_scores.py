from concurrent.futures import ThreadPoolExecutor
from uuid import uuid4
import pytest
from sqlalchemy import func, select
from vatican_rating.application.errors import ApplicationError
from vatican_rating.application.use_cases import RatingUseCases
from vatican_rating.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory
from vatican_rating.persistence.postgres.tables import Leaderboard, LeaderboardScore, RunScoreReceipt


@pytest.fixture
def rating(rating_storage):
    return RatingUseCases(PostgresUnitOfWorkFactory(rating_storage[0]))


def payload(**patch):
    return {'accountId':str(uuid4()),'runId':str(uuid4()),'boardCode':'run-xp','rulesetId':'xp-v1',
            'contentReleaseId':'sha256-test','points':80,**patch}


@pytest.mark.parametrize('patch',[{'points':-1},{'points':True},{'points':'80'},{'points':2**63},
    {'boardCode':''},{'boardCode':'x'*65},{'boardCode':{}},{'rulesetId':None},{'contentReleaseId':'bad/name'},
    {'runId':'invalid'},{'accountId':'invalid'},{'extra':True}])
def test_malformed_messages_never_create_a_score(rating,rating_storage,patch):
    with pytest.raises(ApplicationError) as error:rating.receive(uuid4(),'run.rating',1,payload(**patch))
    assert error.value.status_code==422
    with rating_storage[0].transaction() as session:
        assert session.scalar(select(func.count()).select_from(RunScoreReceipt))==0
        assert session.scalar(select(func.count()).select_from(LeaderboardScore))==0


@pytest.mark.parametrize('event,version',[('unknown',1),('run.rating',True),('run.rating',2),('run.rating','1')])
def test_unsupported_envelopes_are_rejected(rating,event,version):
    with pytest.raises(ApplicationError):rating.receive(uuid4(),event,version,payload())


def test_zero_points_are_a_durable_source_and_cannot_be_replaced_later(rating):
    body=payload(points=0)
    receipt=rating.receive(uuid4(),'run.rating',1,body)['receipt']
    assert receipt['scoreAfter']==0 and rating.ranking('run-xp')['entries'][0]['score']==0
    with pytest.raises(ApplicationError) as error:rating.receive(uuid4(),'run.rating',1,{**body,'points':5})
    assert error.value.status_code==409


def test_out_of_order_runs_add_deltas_and_preserve_original_receipts(rating):
    account=str(uuid4())
    newer=payload(accountId=account,points=20)
    older=payload(accountId=account,points=80)
    receipt=rating.receive(uuid4(),'run.rating',1,newer)['receipt']
    rating.receive(uuid4(),'run.rating',1,older)
    assert rating.ranking('run-xp')['entries'][0]['score']==100
    assert rating.receive(uuid4(),'run.rating',1,newer)['receipt']==receipt
    assert receipt['scoreAfter']==20


def test_concurrent_sources_and_duplicate_aliases_serialize_one_players_total(rating):
    account=str(uuid4())
    bodies=[payload(accountId=account,points=i) for i in range(1,9)]
    with ThreadPoolExecutor(max_workers=8) as pool:
        list(pool.map(lambda body:rating.receive(uuid4(),'run.rating',1,body),bodies*3))
    assert rating.ranking('run-xp')['entries'][0]['score']==36


def test_score_capacity_failure_is_atomic_and_replay_cannot_reroute_to_another_board(rating,rating_storage):
    body=payload(points=2**63-1)
    rating.receive(uuid4(),'run.rating',1,body)
    with pytest.raises(ApplicationError) as error:
        rating.receive(uuid4(),'run.rating',1,payload(accountId=body['accountId'],points=1))
    assert error.value.status_code==409
    assert rating.ranking('run-xp')['entries'][0]['score']==2**63-1
    rating.configure_board('another')
    with pytest.raises(ApplicationError):rating.receive(uuid4(),'run.rating',1,{**body,'boardCode':'another'})
    assert rating.ranking('another')['entries']==[]
    with rating_storage[0].transaction() as session:
        assert session.scalar(select(func.count()).select_from(RunScoreReceipt))==1


def test_board_configuration_is_idempotent_and_incompatible_legacy_source_is_preserved(rating,rating_storage):
    rating.configure_board('season-2');rating.configure_board('season-2')
    with rating_storage[0].transaction() as session:
        session.add(Leaderboard(id=uuid4(),code='legacy',score_source='OTHER'))
    with pytest.raises(ApplicationError) as error:rating.configure_board('legacy')
    assert error.value.status_code==409
    with pytest.raises(ApplicationError):rating.receive(uuid4(),'run.rating',1,payload(boardCode='legacy'))


def test_unknown_board_can_be_configured_then_original_message_replayed(rating):
    body=payload(boardCode='season-next');identity=uuid4()
    with pytest.raises(ApplicationError):rating.receive(identity,'run.rating',1,body)
    rating.configure_board('season-next')
    assert rating.receive(identity,'run.rating',1,body)['receipt']['scoreAfter']==80


def test_concurrent_message_identity_conflict_is_a_business_rejection(rating, rating_storage):
    from threading import Barrier

    identity = uuid4()
    bodies = [payload(points=10), payload(points=20)]
    barrier = Barrier(2)

    def receive(body):
        barrier.wait(timeout=10)
        try:
            return rating.receive(identity, 'run.rating', 1, body)
        except ApplicationError as error:
            assert error.status_code == 409
            return None

    with ThreadPoolExecutor(max_workers=2) as pool:
        results = list(pool.map(receive, bodies))
    accepted = next(result for result in results if result is not None)
    assert sum(result is not None for result in results) == 1
    assert rating.ranking('run-xp')['entries'] == [{
        'accountId': accepted['receipt']['accountId'], 'score': accepted['receipt']['points'], 'position': 1,
    }]
    with rating_storage[0].transaction() as session:
        assert session.scalar(select(func.count()).select_from(RunScoreReceipt)) == 1


@pytest.mark.parametrize('message_id', [None, True, 123, 'invalid'])
def test_invalid_message_identity_is_rejected_before_transaction(message_id):
    def unexpected_uow():
        pytest.fail('Invalid message identity opened a transaction')
    with pytest.raises(ApplicationError) as error:
        RatingUseCases(unexpected_uow).receive(message_id, 'run.rating', 1, payload())
    assert error.value.status_code == 422
