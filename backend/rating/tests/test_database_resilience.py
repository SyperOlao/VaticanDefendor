from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager
from time import monotonic, sleep
from uuid import uuid4

from dishka import Provider, Scope
from fastapi.testclient import TestClient
from psycopg.conninfo import make_conninfo
import pytest
from sqlalchemy import event, text

from vatican_rating.bootstrap.settings import Settings
from vatican_rating.persistence.postgres.database import Database
from vatican_rating.persistence.postgres.repositories.state import StateRepository
from vatican_rating.presentation.app import create_app


SECRET = 's' * 40


def message(**patch):
    return {'messageId': str(uuid4()), 'eventName': 'run.rating', 'schemaVersion': 1, 'payload': {
        'accountId': str(uuid4()), 'runId': str(uuid4()), 'boardCode': 'run-xp',
        'rulesetId': 'xp-v1', 'contentReleaseId': 'release-v1', 'points': 80, **patch,
    }}


def receive(client, body):
    return client.post('/internal/messages', json=body, headers={'X-Service-Secret': SECRET})


@contextmanager
def api(url, **options):
    with TestClient(create_app(Settings(url, SECRET, **options))) as client:
        yield client


def assert_empty(db):
    with db.engine.connect() as connection:
        for table in ('leaderboard_scores', 'run_score_receipts', 'consumed_messages'):
            assert connection.scalar(text(f'SELECT count(*) FROM {table}')) == 0


def assert_retryable(response, code):
    assert response.status_code == 503
    assert response.json()['code'] == code
    assert response.headers['Retry-After'] == '1'
    assert set(response.json()) == {'code', 'detail', 'requestId', 'errors'}


@pytest.mark.parametrize('kind', ['message', 'run', 'player'])
def test_contended_advisory_lock_times_out_rolls_back_and_same_message_recovers(rating_storage, kind):
    from uuid import UUID

    db, url = rating_storage
    body = message()
    identity = UUID(body['messageId'] if kind == 'message' else body['payload']['runId' if kind == 'run' else 'accountId'])
    with api(url, lock_timeout_ms=80, statement_timeout_ms=1000) as client:
        with db.transaction() as session:
            getattr(StateRepository(session), 'lock_' + kind)(identity)
            started = monotonic()
            response = receive(client, body)
            assert monotonic() - started < 3
            assert_retryable(response, 'OperationTimeout')
            assert_empty(db)
            assert client.get('/health/live').status_code == 200
        accepted = receive(client, body)
        assert accepted.status_code == 200
        assert receive(client, body).json() == accepted.json()
        assert client.get('/v1/leaderboards/run-xp').json()['entries'][0]['score'] == 80


@pytest.mark.parametrize('state,code,status', [
    ('40001', 'ConcurrentModification', 503), ('40P01', 'ConcurrentModification', 503),
    ('55P03', 'OperationTimeout', 503), ('57014', 'OperationTimeout', 503),
    ('23514', 'InternalError', 500),
])
def test_postgres_failure_after_score_change_rolls_back_everything_and_is_sanitized(rating_storage, caplog, state, code, status):
    db, url = rating_storage
    body = message()
    # The trigger runs after the score UPSERT, before the transaction can commit.
    with db.engine.begin() as connection:
        connection.execute(text(f"""CREATE FUNCTION injected_rating_failure() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION USING ERRCODE = '{state}', MESSAGE = 'private-database-diagnostic'; END $$"""))
        connection.execute(text('CREATE TRIGGER injected_failure BEFORE INSERT ON run_score_receipts '
                                'FOR EACH ROW EXECUTE FUNCTION injected_rating_failure()'))
    with api(url) as client:
        response = receive(client, body)
        assert response.status_code == status and response.json()['code'] == code
        if status == 503:
            assert response.headers['Retry-After'] == '1'
        assert 'private-database-diagnostic' not in response.text + caplog.text
        assert body['payload']['accountId'] not in response.text + caplog.text
        assert_empty(db)
        with db.engine.begin() as connection:
            connection.execute(text('DROP TRIGGER injected_failure ON run_score_receipts'))
        accepted = receive(client, body)
        assert accepted.status_code == 200
        assert receive(client, body).json() == accepted.json()


def test_slow_statement_is_cancelled_without_partial_credit_and_connection_is_reusable(rating_storage):
    db, url = rating_storage
    body = message()
    with db.engine.begin() as connection:
        connection.execute(text("""CREATE FUNCTION injected_slow_receipt() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN PERFORM pg_sleep(5); RETURN NEW; END $$"""))
        connection.execute(text('CREATE TRIGGER injected_slow BEFORE INSERT ON run_score_receipts '
                                'FOR EACH ROW EXECUTE FUNCTION injected_slow_receipt()'))
    with api(url, lock_timeout_ms=50, statement_timeout_ms=100) as client:
        started = monotonic()
        assert_retryable(receive(client, body), 'OperationTimeout')
        assert monotonic() - started < 3
        assert_empty(db)
        with db.engine.begin() as connection:
            connection.execute(text('DROP TRIGGER injected_slow ON run_score_receipts'))
        assert receive(client, body).status_code == 200
        assert client.get('/health/ready').status_code == 200


def test_terminated_database_connection_rolls_back_and_pool_reconnects(rating_storage):
    admin, url = rating_storage
    runtime = Database(url, lock_timeout_ms=100, statement_timeout_ms=1000)
    override = Provider()
    override.provide(lambda: runtime, provides=Database, scope=Scope.APP)
    body = message()
    killed = []

    def disconnect(connection, cursor, statement, parameters, context, executemany):
        if killed or not statement.startswith('INSERT INTO leaderboard_scores'):
            return
        killed.append(connection.connection.driver_connection.info.backend_pid)
        with admin.engine.begin() as control:
            assert control.scalar(text('SELECT pg_terminate_backend(:pid)'), {'pid': killed[0]})

    event.listen(runtime.engine, 'after_cursor_execute', disconnect)
    try:
        with TestClient(create_app(Settings(url, SECRET), providers=(override,))) as client:
            assert_retryable(receive(client, body), 'DatabaseUnavailable')
            assert len(killed) == 1
            assert_empty(admin)
            accepted = receive(client, body)
            assert accepted.status_code == 200 and accepted.json()['receipt']['scoreAfter'] == 80
            assert receive(client, body).json() == accepted.json()
            assert client.get('/health/ready').status_code == 200
    finally:
        event.remove(runtime.engine, 'after_cursor_execute', disconnect)
        runtime.close()


def test_saturated_pool_returns_retryable_failure_and_recovers(rating_storage):
    db, url = rating_storage
    with api(url, pool_size=1) as client:
        runtime = client.app.state.dishka_container.get(Database)
        with runtime.engine.connect():
            assert_retryable(client.get('/health/ready'), 'DatabaseUnavailable')
            assert client.get('/health/live').status_code == 200
        assert client.get('/health/ready').status_code == 200
        assert_empty(db)


def wait_for_lock(db, application_name):
    deadline = monotonic() + 3
    while monotonic() < deadline:
        with db.engine.connect() as connection:
            if connection.scalar(text("SELECT EXISTS (SELECT 1 FROM pg_stat_activity "
                                      "WHERE application_name=:name AND wait_event_type='Lock')"),
                                 {'name': application_name}):
                return
        sleep(.01)
    pytest.fail('Rating request did not wait for the held database lock')


def test_atomic_increment_preserves_an_update_committed_while_it_waits_for_the_row(rating_storage):
    db, url = rating_storage
    application_name = 'rating-resilience-' + uuid4().hex
    url = make_conninfo(url, application_name=application_name)
    first = message()
    second = message(accountId=first['payload']['accountId'])
    with api(url, lock_timeout_ms=5000, statement_timeout_ms=10000) as client:
        assert receive(client, first).status_code == 200
        with ThreadPoolExecutor(max_workers=1) as pool:
            with db.engine.begin() as connection:
                connection.execute(text('UPDATE leaderboard_scores SET score=score+20'))
                pending = pool.submit(receive, client, second)
                wait_for_lock(db, application_name)
            accepted = pending.result(timeout=10)
        assert accepted.status_code == 200 and accepted.json()['receipt']['scoreAfter'] == 180
        assert receive(client, second).json() == accepted.json()
        assert client.get('/v1/leaderboards/run-xp').json()['entries'][0]['score'] == 180


def test_row_lock_timeout_does_not_store_a_source_or_message_alias(rating_storage):
    db, url = rating_storage
    first = message()
    second = message(accountId=first['payload']['accountId'])
    with api(url, lock_timeout_ms=80, statement_timeout_ms=1000) as client:
        assert receive(client, first).status_code == 200
        with db.engine.begin() as connection:
            connection.execute(text('UPDATE leaderboard_scores SET score=score+20'))
            assert_retryable(receive(client, second), 'OperationTimeout')
            with db.engine.connect() as check:
                assert check.scalar(text('SELECT count(*) FROM run_score_receipts')) == 1
                assert check.scalar(text('SELECT count(*) FROM consumed_messages')) == 1
        accepted = receive(client, second)
        assert accepted.status_code == 200 and accepted.json()['receipt']['scoreAfter'] == 180


def test_environment_runtime_budgets_are_applied_only_to_the_api(monkeypatch, rating_storage):
    db, url = rating_storage
    for name, value in {'VD_RATING_DATABASE_URL': url, 'VD_SERVICE_SECRET': SECRET,
                        'VD_RATING_DB_POOL_SIZE': '2', 'VD_RATING_DB_LOCK_TIMEOUT_MS': '80',
                        'VD_RATING_DB_STATEMENT_TIMEOUT_MS': '1000'}.items():
        monkeypatch.setenv(name, value)
    settings = Settings.from_environment()
    assert (settings.pool_size, settings.lock_timeout_ms, settings.statement_timeout_ms) == (2, 80, 1000)
    with TestClient(create_app(settings)) as client:
        runtime = client.app.state.dishka_container.get(Database)
        with runtime.engine.connect() as connection:
            assert connection.scalar(text('SHOW lock_timeout')) == '80ms'
            assert connection.scalar(text('SHOW statement_timeout')) == '1s'
            assert connection.scalar(text('SHOW idle_in_transaction_session_timeout')) == '15s'
    with db.engine.connect() as connection:
        assert connection.scalar(text('SHOW statement_timeout')) == '0'
        assert connection.scalar(text('SHOW lock_timeout')) == '0'


@pytest.mark.parametrize('damage', ['missing', 'empty', 'wrong', 'multiple'])
def test_readiness_rejects_incomplete_or_ambiguous_schema_and_recovers(rating_storage, damage):
    db, url = rating_storage
    with db.engine.begin() as connection:
        if damage == 'missing':
            connection.execute(text('ALTER TABLE alembic_version RENAME TO saved_alembic_version'))
        elif damage == 'empty':
            connection.execute(text('DELETE FROM alembic_version'))
        elif damage == 'wrong':
            connection.execute(text("UPDATE alembic_version SET version_num='unexpected'"))
        else:
            connection.execute(text("INSERT INTO alembic_version VALUES ('unexpected')"))
    with api(url) as client:
        assert client.get('/health/ready').status_code == 503
        assert client.get('/health/live').status_code == 200
        with db.engine.begin() as connection:
            if damage == 'missing':
                connection.execute(text('ALTER TABLE saved_alembic_version RENAME TO alembic_version'))
            else:
                connection.execute(text('DELETE FROM alembic_version'))
                from vatican_rating.migrate import SCHEMA_REVISION
                connection.execute(text('INSERT INTO alembic_version VALUES (:revision)'), {'revision': SCHEMA_REVISION})
        assert client.get('/health/ready').status_code == 200
