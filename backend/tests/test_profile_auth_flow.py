"""Real opaque auth sessions keep the same game state through account lifecycle."""
import os
import sys
from pathlib import Path
from uuid import UUID, uuid4
import httpx
from fastapi.testclient import TestClient
from sqlalchemy import create_engine, text
from sqlalchemy.engine import make_url
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'auth'))
from vatican_auth.bootstrap.settings import AuthSettings
from vatican_auth.presentation.app import create_app as auth_app
from vatican_auth.migrate import migrate as migrate_auth
from vatican_backend.api import create_app
from vatican_backend.bootstrap.settings import Settings
from vatican_backend.integrations.http import AuthHttpGateway
from vatican_backend.application.energy import ConfigureEnergy
from vatican_backend.domain.energy import EnergyRules
from vatican_backend.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory
from vatican_wallet.application.use_cases import WalletUseCases
from vatican_wallet.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as WalletUow


def test_guest_registration_login_refresh_recovery_preserve_profile_energy_and_starting_money(service_storage_factory):
    game_db,game_url=service_storage_factory('game_backend')
    wallet_db,_=service_storage_factory('wallet')
    root=make_url(os.environ['VD_TEST_DATABASE_URL']).set(drivername='postgresql+psycopg')
    admin=create_engine(root);schema='profile_auth_'+uuid4().hex
    with admin.begin() as connection:connection.execute(text(f'CREATE SCHEMA "{schema}"'))
    auth_url=root.update_query_dict({'options':'-c search_path='+schema}).render_as_string(hide_password=False)
    try:
        migrate_auth(auth_url)
        with TestClient(auth_app(AuthSettings(auth_url)),client=('127.0.0.1',12345)) as auth:
            def handler(request):
                assert game_db.engine.pool.checkedout()==0
                result=auth.get(request.url.path,headers=dict(request.headers))
                return httpx.Response(result.status_code,json=result.json())
            with httpx.Client(transport=httpx.MockTransport(handler)) as connection:
                app=create_app(Settings(game_url,{},120,energy_ruleset_id='profile-auth-energy'),
                               auth_gateway=AuthHttpGateway(connection,'http://auth'))
                ConfigureEnergy(PostgresUnitOfWorkFactory(game_db)).execute(EnergyRules('profile-auth-energy',100,60000,1,5))
                with TestClient(app) as game:
                    guest=auth.post('/v1/guests').json();account=UUID(guest['accountId'])
                    headers={'Authorization':'Bearer '+guest['token']}
                    first=game.get('/v1/profile',headers=headers).json()
                    avatars=game.get('/v1/profile/avatars',headers=headers).json()['avatars']
                    saved=game.patch('/v1/profile',headers=headers,json={'expectedVersion':first['version'],
                        'displayName':'Гость-рыцарь','avatarAssetKey':avatars[0]['assetKey']}).json()
                    assert saved['accountId']==guest['accountId'] and saved['version']==2
                    energy=game.get('/v1/energy',headers=headers).json()
                    wallet=WalletUseCases(WalletUow(wallet_db));money=wallet.balance(account)
                    assert money['balance']==500
                    credentials={'login':'profile_player','password':'profile-password-123'}
                    registered=auth.post('/v1/auth/register',json={**credentials,'guestToken':guest['token']})
                    assert registered.status_code==201
                    registered=registered.json()
                    assert registered['accountId']==guest['accountId'] and registered['accessExpiresAt']
                    headers={'Authorization':'Bearer '+registered['accessToken']}
                    assert game.get('/v1/profile',headers=headers).json()==saved
                    assert game.get('/v1/energy',headers=headers).json()['currentEnergy']==energy['currentEnergy']
                    assert wallet.balance(account)==money
                    assert auth.post('/v1/auth/logout',headers=headers).status_code==204
                    assert game.get('/v1/profile',headers=headers).status_code==401
                    logged=auth.post('/v1/auth/login',json=credentials).json()
                    refreshed=auth.post('/v1/auth/refresh',json={'refreshToken':logged['refreshToken']})
                    assert refreshed.status_code==200
                    assert auth.post('/v1/auth/refresh',json={'refreshToken':logged['refreshToken']}).status_code==401
                    headers={'Authorization':'Bearer '+refreshed.json()['accessToken']}
                    assert game.get('/v1/profile',headers=headers).json()==saved
                    outsider=auth.post('/v1/auth/register',json={'login':'another_player','password':credentials['password']}).json()
                    other_headers={'Authorization':'Bearer '+outsider['accessToken']}
                    other=game.get('/v1/profile',headers=other_headers).json()
                    assert other['accountId']!=saved['accountId'] and other['avatarAssetKey'] is None
                    assert game.patch('/v1/profile',headers=other_headers,json={'expectedVersion':other['version'],
                        'accountId':saved['accountId'],'displayName':'Intruder'}).status_code==422
                    recovered=auth.post('/v1/auth/recover',json={'login':credentials['login'],
                        'password':'new-profile-password-123','recoveryCode':registered['recoveryCode']})
                    assert recovered.status_code==200 and recovered.json()['accountId']==guest['accountId']
                    assert game.get('/v1/profile',headers=headers).status_code==401
                    headers={'Authorization':'Bearer '+recovered.json()['accessToken']}
                    assert game.get('/v1/profile',headers=headers).json()==saved
                    assert wallet.balance(account)==money
    finally:
        with admin.begin() as connection:connection.execute(text(f'DROP SCHEMA "{schema}" CASCADE'))
        admin.dispose()
