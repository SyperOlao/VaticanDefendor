"""Each independently built service uses the same live auth gRPC contract."""
from dataclasses import replace
import importlib
import os
from types import SimpleNamespace
from uuid import UUID, uuid4

from fastapi.testclient import TestClient
import pytest
from sqlalchemy import create_engine, text
from sqlalchemy.engine import make_url

from vatican_auth.bootstrap.grpc import AuthGrpcSettings
from vatican_auth.bootstrap.settings import AuthSettings
from vatican_auth.application.catalog import ROLE_MAP
from vatican_auth.migrate import migrate
from vatican_auth.presentation.app import create_app

SECRET = 'interservice-auth-tests-' + 's' * 40
PACKAGES = ['vatican_backend', 'vatican_shop', 'vatican_wallet', 'vatican_inventory']


@pytest.fixture
def auth_storage():
    url = os.getenv('VD_TEST_DATABASE_URL')
    if not url:
        pytest.skip('Set VD_TEST_DATABASE_URL to a disposable PostgreSQL database')
    admin = create_engine(make_url(url).set(drivername='postgresql+psycopg'))
    schema = 'auth_transport_' + uuid4().hex
    with admin.begin() as connection:
        connection.execute(text(f'CREATE SCHEMA "{schema}"'))
    scoped = admin.url.update_query_dict({'options': '-c search_path=' + schema})
    db = create_engine(scoped)
    try:
        migrate(scoped.render_as_string(hide_password=False))
        yield db, scoped.render_as_string(hide_password=False)
    finally:
        db.dispose()
        with admin.begin() as connection:
            connection.execute(text(f'DROP SCHEMA "{schema}" CASCADE'))
        admin.dispose()


@pytest.mark.parametrize('package', PACKAGES)
def test_guest_upgrade_fresh_roles_revocation_and_service_failures(package, auth_storage):
    module = importlib.import_module(package + '.integrations.auth_grpc')
    error_type = importlib.import_module(package + '.application.errors').ApplicationError
    db, url = auth_storage
    with TestClient(create_app(AuthSettings(url), grpc_settings=AuthGrpcSettings(True, '127.0.0.1:0', SECRET)), client=('127.0.0.1', 12500)) as auth:
        options = SimpleNamespace(service_secret=SECRET, auth_grpc_target=f'127.0.0.1:{auth.app.state.auth_grpc_port}',
            auth_grpc_timeout_ms=300, auth_grpc_attempts=2, auth_grpc_ca_file='')
        gateway = module.AuthGrpcGateway(options)
        bad_gateway = module.AuthGrpcGateway(SimpleNamespace(**{**vars(options), 'service_secret': 'wrong-' + 's' * 40}))
        try:
            guest = auth.post('/v1/guests').json()
            principal = gateway.authenticate(guest['token'])
            assert principal.account_id == UUID(guest['accountId']) and principal.is_guest
            registered = auth.post('/v1/auth/register', json={'login': 'rpc_player', 'password': 'Password123!', 'guestToken': guest['token']})
            assert registered.status_code == 201
            value = registered.json()
            assert gateway.authenticate(value['accessToken']).account_id == principal.account_id
            with pytest.raises(error_type) as error:
                gateway.authenticate(guest['token'])
            assert error.value.status_code == 401
            with pytest.raises(error_type) as error:
                bad_gateway.authenticate(value['accessToken'])
            assert error.value.status_code == 503
            if package == 'vatican_shop':
                assert gateway.authenticate(value['accessToken']).roles == ()
                with db.begin() as connection:
                    connection.execute(text('INSERT INTO roles (code,description) VALUES (:code, :description) ON CONFLICT DO NOTHING'),
                                       {'code': ROLE_MAP['admin'].code, 'description': 'Test administrator'})
                    connection.execute(text('INSERT INTO account_roles (id,account_id,role_id) '
                        'SELECT :id,:account,id FROM roles WHERE code=:code'),
                        {'id': uuid4(), 'account': principal.account_id, 'code': ROLE_MAP['admin'].code})
                assert gateway.authenticate(value['accessToken']).roles == (ROLE_MAP['admin'].code,)
                with db.begin() as connection:
                    connection.execute(text('UPDATE account_roles SET revoked_at=now()'))
                assert gateway.authenticate(value['accessToken']).roles == ()
            assert auth.post('/v1/auth/logout', headers={'Authorization': 'Bearer ' + value['accessToken']}).status_code == 204
            with pytest.raises(error_type) as error:
                gateway.authenticate(value['accessToken'])
            assert error.value.status_code == 401
        finally:
            gateway.close()
            bad_gateway.close()
    # A closed/down auth listener never becomes a valid account or an HTTP fallback.
    unavailable = module.AuthGrpcGateway(options)
    try:
        with pytest.raises(error_type) as error:
            unavailable.authenticate('a' * 43)
        assert error.value.status_code == 503
    finally:
        unavailable.close()


@pytest.mark.parametrize('package', PACKAGES)
def test_invalid_rpc_configuration_fails_at_startup(package):
    settings = importlib.import_module(package + '.bootstrap.settings').Settings
    base = settings('postgresql://unused', {}, 120) if package == 'vatican_backend' else settings('postgresql://unused', SECRET)
    for patch in [dict(auth_transport='other'), dict(auth_transport='grpc', auth_grpc_target='http://auth:8080'),
                  dict(auth_transport='grpc', service_secret='short'), dict(auth_grpc_timeout_ms=0), dict(auth_grpc_attempts=10)]:
        with pytest.raises(ValueError):
            replace(base, **patch)
