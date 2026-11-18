"""Actual auth policy, PostgreSQL, HTTP facade and gRPC crash recovery."""
from contextlib import contextmanager
from dataclasses import replace
import os
import shutil
from pathlib import Path
import socket
import subprocess
import sys
import time
from uuid import UUID, uuid4

from fastapi.testclient import TestClient
import grpc
import httpx
import pytest
from sqlalchemy import create_engine, text
from sqlalchemy.engine import make_url

from vatican_auth.bootstrap.grpc import AuthGrpcSettings
from vatican_auth.bootstrap.settings import AuthSettings
from vatican_auth.migrate import migrate
from vatican_auth.presentation.app import create_app as auth_app
from vatican_auth.persistence.postgres.database import Database as AuthDatabase
from vatican_rating.application.errors import ApplicationError
from vatican_rating.bootstrap.settings import Settings
from vatican_rating.integrations.auth_grpc import AuthGrpcGateway
from vatican_rating.integrations.auth_contract import auth_pb2, auth_pb2_grpc
from vatican_rating.presentation.app import create_app as rating_app

SECRET = 'auth-rating-grpc-integration-' + 's' * 32


@pytest.fixture
def auth_storage():
    url = os.environ.get('VD_TEST_DATABASE_URL')
    if not url:
        pytest.skip('Set VD_TEST_DATABASE_URL to a disposable PostgreSQL database')
    root = make_url(url).set(drivername='postgresql+psycopg')
    admin = create_engine(root)
    schema = 'auth_rpc_' + uuid4().hex
    scoped = root.update_query_dict({'options': '-c search_path=' + schema})
    database = create_engine(scoped)
    try:
        with admin.begin() as connection:
            connection.execute(text(f'CREATE SCHEMA "{schema}"'))
        url = scoped.render_as_string(hide_password=False)
        migrate(url)
        yield database, url
    finally:
        database.dispose()
        with admin.begin() as connection:
            connection.execute(text(f'DROP SCHEMA IF EXISTS "{schema}" CASCADE'))
        admin.dispose()


@contextmanager
def real_auth(url, **grpc_options):
    settings = AuthGrpcSettings(True, '127.0.0.1:0', SECRET, **grpc_options)
    with TestClient(auth_app(AuthSettings(url), grpc_settings=settings), client=('127.0.0.1', 12500)) as client:
        yield client


def rating_settings(url, port, **options):
    return Settings(url, SECRET, auth_grpc_target=f'127.0.0.1:{port}', auth_grpc_timeout_ms=500, **options)


def assert_status(call, status):
    with pytest.raises(ApplicationError) as error:
        call()
    assert error.value.status_code == status


def test_same_auth_policy_for_guests_registered_sessions_revocation_and_bans(auth_storage, rating_storage):
    database, url = auth_storage
    with real_auth(url) as auth:
        settings = rating_settings(rating_storage[1], auth.app.state.auth_grpc_port)
        with TestClient(rating_app(settings)) as rating:
            guest = auth.post('/v1/guests').json()
            headers = {'Authorization': 'Bearer ' + guest['token']}
            for _ in range(3):
                reply = rating.get('/v1/leaderboards/run-xp/me', headers=headers)
                assert reply.status_code == 200
                assert reply.json() == {'board': 'run-xp', 'entry': None}
            registered = auth.post('/v1/auth/register', json={
                'login': 'grpc_player', 'password': 'correct-password-123', 'guestToken': guest['token']})
            assert registered.status_code == 201
            credentials = registered.json()
            assert credentials['accountId'] == guest['accountId']
            headers = {'Authorization': 'Bearer ' + credentials['accessToken']}
            assert rating.get('/v1/leaderboards/run-xp/me', headers=headers).status_code == 200
            assert auth.post('/v1/auth/logout', headers=headers).status_code == 204
            assert rating.get('/v1/leaderboards/run-xp/me', headers=headers).status_code == 401
            refresh = {'Authorization': 'Bearer ' + credentials['refreshToken']}
            assert rating.get('/v1/leaderboards/run-xp/me', headers=refresh).status_code == 401
            logged = auth.post('/v1/auth/login', json={'login': 'grpc_player', 'password': 'correct-password-123'}).json()
            headers = {'Authorization': 'Bearer ' + logged['accessToken']}
            assert rating.get('/v1/leaderboards/run-xp/me', headers=headers).status_code == 200
            with database.begin() as connection:
                connection.execute(text("UPDATE accounts SET account_status='BANNED' WHERE id=:id"), {'id': UUID(guest['accountId'])})
            assert rating.get('/v1/leaderboards/run-xp/me', headers=headers).status_code == 401
            assert rating.get('/v1/leaderboards/run-xp').status_code == 200


def test_expired_guest_is_rejected_without_cached_identity(auth_storage):
    database, url = auth_storage
    with real_auth(url) as auth:
        guest = auth.post('/v1/guests').json()
        gateway = AuthGrpcGateway(rating_settings('postgresql://unused', auth.app.state.auth_grpc_port))
        try:
            assert gateway.authenticate(guest['token']).is_guest
            with database.begin() as connection:
                connection.execute(text("UPDATE guest_credentials SET expires_at=now()-interval '1 second'"))
            assert_status(lambda: gateway.authenticate(guest['token']), 401)
        finally:
            gateway.close()


def test_service_credentials_and_token_metadata_fail_closed(auth_storage):
    with real_auth(auth_storage[1]) as auth:
        guest = auth.post('/v1/guests').json()
        with grpc.insecure_channel(f'127.0.0.1:{auth.app.state.auth_grpc_port}') as channel:
            stub = auth_pb2_grpc.AuthServiceStub(channel)
            valid = (('x-service-secret', SECRET), ('authorization', 'Bearer ' + guest['token']))
            reply = stub.Authenticate(auth_pb2.AuthenticateRequest(), metadata=valid, timeout=1)
            assert reply.account_id == guest['accountId'] and reply.HasField('is_guest') and reply.is_guest
            for metadata, code in [
                ((), grpc.StatusCode.PERMISSION_DENIED),
                ((('x-service-secret', 'wrong'),), grpc.StatusCode.PERMISSION_DENIED),
                (valid + (('x-service-secret', SECRET),), grpc.StatusCode.PERMISSION_DENIED),
                ((('x-service-secret', SECRET),), grpc.StatusCode.UNAUTHENTICATED),
                (valid + (('authorization', 'Bearer another-token'),), grpc.StatusCode.UNAUTHENTICATED),
            ]:
                with pytest.raises(grpc.RpcError) as error:
                    stub.Authenticate(auth_pb2.AuthenticateRequest(), metadata=metadata, timeout=1)
                assert error.value.code() == code
        bad = replace(rating_settings('postgresql://unused', auth.app.state.auth_grpc_port), service_secret='x' * 40)
        gateway = AuthGrpcGateway(bad)
        try:
            assert_status(lambda: gateway.authenticate(guest['token']), 503)
        finally:
            gateway.close()


def test_database_failure_becomes_safe_unavailable_and_recovers(auth_storage):
    database, url = auth_storage
    with real_auth(url) as auth:
        guest = auth.post('/v1/guests').json()
        gateway = AuthGrpcGateway(rating_settings('postgresql://unused', auth.app.state.auth_grpc_port))
        try:
            assert gateway.authenticate(guest['token']).account_id == UUID(guest['accountId'])
            with database.begin() as connection:
                connection.execute(text('ALTER TABLE guest_credentials RENAME TO saved_guest_credentials'))
            try:
                assert_status(lambda: gateway.authenticate(guest['token']), 503)
            finally:
                with database.begin() as connection:
                    connection.execute(text('ALTER TABLE saved_guest_credentials RENAME TO guest_credentials'))
            assert gateway.authenticate(guest['token']).account_id == UUID(guest['accountId'])
        finally:
            gateway.close()


def test_expired_rpc_deadline_cancels_blocked_database_work_and_releases_connection(auth_storage):
    database, url = auth_storage
    with real_auth(url) as auth:
        guest = auth.post('/v1/guests').json()
        settings = replace(rating_settings('postgresql://unused', auth.app.state.auth_grpc_port), auth_grpc_timeout_ms=120)
        gateway = AuthGrpcGateway(settings)
        runtime = auth.portal.call(auth.app.state.dishka_container.get, AuthDatabase)
        try:
            with database.begin() as connection:
                connection.execute(text('SELECT pg_advisory_xact_lock(691750363)'))
                assert_status(lambda: gateway.authenticate(guest['token']), 503)
                deadline = time.monotonic() + 2
                while runtime.engine.pool.checkedout():
                    assert time.monotonic() < deadline, 'Cancelled RPC retained a database connection/transaction'
                    time.sleep(.02)
            assert gateway.authenticate(guest['token']).account_id == UUID(guest['accountId'])
        finally:
            gateway.close()


def test_tls_channel_validates_server_certificate_and_plaintext_fails_closed(auth_storage, tmp_path):
    openssl = shutil.which('openssl')
    if openssl is None:
        pytest.skip('TLS contract test requires openssl')
    cert, key = tmp_path / 'server.crt', tmp_path / 'server.key'
    subprocess.run([openssl, 'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1',
        '-keyout', str(key), '-out', str(cert), '-subj', '/CN=localhost',
        '-addext', 'subjectAltName=DNS:localhost,IP:127.0.0.1'], check=True, capture_output=True)
    with real_auth(auth_storage[1], certificate_file=str(cert), private_key_file=str(key)) as auth:
        guest = auth.post('/v1/guests').json()
        settings = rating_settings('postgresql://unused', auth.app.state.auth_grpc_port)
        for ca_file, succeeds in ((str(cert), True), ('', False)):
            gateway = AuthGrpcGateway(replace(settings, auth_grpc_ca_file=ca_file))
            try:
                if succeeds:
                    assert gateway.authenticate(guest['token']).account_id == UUID(guest['accountId'])
                else:
                    assert_status(lambda: gateway.authenticate(guest['token']), 503)
            finally:
                gateway.close()


def free_port():
    with socket.socket() as listener:
        listener.bind(('127.0.0.1', 0))
        return listener.getsockname()[1]


def test_auth_process_sigkill_then_restart_recovers_without_restarting_rating(auth_storage, rating_storage, tmp_path):
    http_port, rpc_port = free_port(), free_port()
    env = {**os.environ, 'PYTHONPATH': str(Path(__file__).resolve().parents[2] / 'auth'),
        'VD_AUTH_DATABASE_URL': auth_storage[1], 'VD_SERVICE_SECRET': SECRET,
        'VD_AUTH_PORT': str(http_port), 'VD_AUTH_GRPC_ENABLED': 'true',
        'VD_AUTH_GRPC_BIND': f'127.0.0.1:{rpc_port}'}
    processes = []
    log_path = tmp_path / 'auth-process.log'

    def start():
        with log_path.open('ab') as log:
            process = subprocess.Popen([sys.executable, '-m', 'vatican_auth'], env=env, stdout=log, stderr=log)
        processes.append(process)
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            assert process.poll() is None, 'Disposable auth process exited before startup'
            try:
                if httpx.get(f'http://127.0.0.1:{http_port}/health/ready', timeout=.5, trust_env=False).status_code == 200:
                    return process
            except httpx.HTTPError:
                pass
            time.sleep(.05)
        pytest.fail('Disposable auth process did not become ready')

    try:
        process = start()
        guest = httpx.post(f'http://127.0.0.1:{http_port}/v1/guests', timeout=3, trust_env=False).json()
        headers = {'Authorization': 'Bearer ' + guest['token']}
        with TestClient(rating_app(rating_settings(rating_storage[1], rpc_port))) as rating:
            assert rating.get('/v1/leaderboards/run-xp/me', headers=headers).status_code == 200
            process.kill()
            process.wait(timeout=5)
            start_time = time.monotonic()
            failure = rating.get('/v1/leaderboards/run-xp/me', headers=headers)
            assert failure.status_code == 503 and failure.headers['Retry-After'] == '1'
            assert failure.json()['code'] == 'ServiceUnavailable'
            assert time.monotonic() - start_time < 2
            assert rating.get('/health/ready').status_code == 200
            assert rating.get('/v1/leaderboards/run-xp').status_code == 200
            process = start()
            deadline = time.monotonic() + 4
            while True:
                reply = rating.get('/v1/leaderboards/run-xp/me', headers=headers)
                if reply.status_code == 200:
                    break
                assert time.monotonic() < deadline, 'Existing rating channel did not reconnect'
                time.sleep(.05)
            assert reply.json() == {'board': 'run-xp', 'entry': None}
        assert guest['token'] not in log_path.read_text() and SECRET not in log_path.read_text()
    finally:
        for process in processes:
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=5)


def test_rabbitmq_scoring_and_public_reads_continue_when_auth_is_unavailable(amqp_world):
    from .conftest import complete
    w = amqp_world
    settings = rating_settings(w.rating_url, free_port())
    with TestClient(rating_app(settings)) as rating:
        failure = rating.get('/v1/leaderboards/run-xp/me', headers={'Authorization': 'Bearer unavailable-auth'})
        assert failure.status_code == 503
        complete(w)
        # Redeliver the original Rabbit command: the original receipt prevents double XP.
        w.sender.publish_command({'id': w.source, 'event_name': 'run.rating', 'schema_version': 1, 'payload': w.payload})
        assert w.receiver.receive_once(w.accept)
        assert not w.process.once()
        assert w.publish.once() and w.sender.receive_once(w.result)
        result = rating.get('/v1/leaderboards/run-xp')
        assert result.status_code == 200
        assert result.json()['entries'][0]['score'] == 80
