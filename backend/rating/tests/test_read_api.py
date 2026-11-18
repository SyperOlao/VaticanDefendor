from uuid import UUID, uuid4

from fastapi.testclient import TestClient
import pytest
from sqlalchemy import select, text

from vatican_rating.application.errors import ApplicationError
from vatican_rating.application.models import Principal
from vatican_rating.application.use_cases import RatingUseCases
from vatican_rating.bootstrap.settings import Settings
from vatican_rating.persistence.postgres.tables import Leaderboard
from vatican_rating.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory
from vatican_rating.presentation.app import create_app


@pytest.fixture
def rating(rating_storage):
    return RatingUseCases(PostgresUnitOfWorkFactory(rating_storage[0]))


def contribute(rating, account, points, board='run-xp'):
    return rating.receive(uuid4(), 'run.rating', 1, {
        'accountId': str(account), 'runId': str(uuid4()), 'boardCode': board,
        'rulesetId': 'xp-v1', 'contentReleaseId': 'release-v1', 'points': points,
    })


def test_boards_include_empty_and_imported_sources_in_code_order(rating, rating_storage):
    rating.configure_board('season-2')
    with rating_storage[0].transaction() as session:
        session.add(Leaderboard(id=uuid4(), code='ALL_TIME_XP', score_source='TOTAL_XP'))
    assert rating.boards()['boards'] == [
        {'code': 'ALL_TIME_XP', 'scoreSource': 'TOTAL_XP'},
        {'code': 'run-xp', 'scoreSource': 'RUN_XP'},
        {'code': 'season-2', 'scoreSource': 'RUN_XP'},
    ]
    assert rating.boards(1, 1)['boards'] == [{'code': 'run-xp', 'scoreSource': 'RUN_XP'}]
    assert rating.boards(1, 3)['boards'] == []


def test_positions_match_pages_including_ties_zero_and_other_boards(rating):
    accounts = [UUID(int=i) for i in range(1, 5)]
    for account, points in zip(reversed(accounts), (0, 10, 10, 20)):
        contribute(rating, account, points)
    rating.configure_board('other')
    contribute(rating, accounts[-1], 999, 'other')
    page = rating.ranking('run-xp', 2, 1)
    for entry in page['entries']:
        assert rating.standing('run-xp', UUID(entry['accountId'])) == {'board': 'run-xp', 'entry': entry}
    assert rating.standing('run-xp', accounts[-1])['entry'] == {
        'accountId': str(accounts[-1]), 'score': 0, 'position': 4,
    }
    contribute(rating, accounts[-1], 30)
    assert rating.standing('run-xp', accounts[-1])['entry']['position'] == 1
    assert rating.standing('other', accounts[-1])['entry']['score'] == 999


def test_unranked_player_is_distinct_from_missing_board_and_read_creates_nothing(rating, rating_storage):
    account = uuid4()
    assert rating.standing('run-xp', account) == {'board': 'run-xp', 'entry': None}
    assert rating.ranking('run-xp')['entries'] == []
    with pytest.raises(ApplicationError) as error:
        rating.standing('missing', account)
    assert error.value.status_code == 404
    with rating_storage[0].engine.connect() as connection:
        assert connection.scalar(text('SELECT count(*) FROM leaderboard_scores')) == 0
        assert connection.scalar(text('SELECT count(*) FROM run_score_receipts')) == 0


def test_own_position_is_available_beyond_offset_limit(rating, rating_storage):
    account = UUID(int=2**128 - 1)
    contribute(rating, account, 0)
    with rating_storage[0].transaction() as session:
        board = session.scalar(select(Leaderboard.id).where(Leaderboard.code == 'run-xp'))
        session.execute(text("""INSERT INTO leaderboard_scores (board_id, account_id, score)
            SELECT :board, CAST(lpad(to_hex(n), 32, '0') AS uuid), 1
            FROM generate_series(1, 100001) AS n"""), {'board': board})
    assert rating.standing('run-xp', account)['entry'] == {
        'accountId': str(account), 'score': 0, 'position': 100002,
    }


def test_own_endpoint_resolves_auth_owner_and_keeps_public_reads_independent(rating, rating_storage):
    account, other = uuid4(), uuid4()
    contribute(rating, account, 10)
    contribute(rating, other, 20)

    class Auth:
        calls = []

        def authenticate(self, token):
            self.calls.append(token)
            if token == 'down':
                raise ApplicationError(503, 'Auth service is unavailable')
            if token != 'valid':
                raise ApplicationError(401, 'Credential is invalid or expired')
            return Principal(account, True)

    auth = Auth()
    with TestClient(create_app(Settings(rating_storage[1], 's' * 32), auth_gateway=auth)) as client:
        endpoint = '/v1/leaderboards/run-xp/me'
        for authorization in (None, 'Basic valid', 'Bearer ', 'Bearer two tokens', 'Bearer ' + 'a' * 513):
            response = client.get(endpoint, headers={'Authorization': authorization} if authorization else {})
            assert response.status_code == 401
            assert set(response.json()) == {'code', 'detail', 'requestId', 'errors'}
        assert auth.calls == []
        response = client.get(endpoint, headers={'Authorization': 'Bearer valid'}, params={'accountId': str(other)})
        assert response.status_code == 200
        assert response.json()['entry'] == {'accountId': str(account), 'score': 10, 'position': 2}
        assert client.get(endpoint, headers={'Authorization': 'Bearer invalid'}).status_code == 401
        assert client.get(endpoint, headers={'Authorization': 'Bearer down'}).status_code == 503
        calls = list(auth.calls)
        assert client.get('/v1/leaderboards').status_code == 200
        assert client.get('/v1/leaderboards/run-xp').status_code == 200
        assert auth.calls == calls
        assert client.get('/v1/leaderboards/missing/me').status_code == 401
        assert client.get('/v1/leaderboards/missing/me', headers={'Authorization': 'Bearer valid'}).status_code == 404
        assert client.get('/v1/leaderboards', params={'limit': 101}).status_code == 422
        assert client.get('/v1/leaderboards', params={'offset': -1}).status_code == 422
        assert client.get('/v1/leaderboards', params={'offset': 100000}).json() == {'boards': []}
        spec = client.get('/openapi.json').json()
        assert spec['paths']['/v1/leaderboards/{code}/me']['get']['security'] == [{'HTTPBearer': []}]
