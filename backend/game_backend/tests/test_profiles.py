from concurrent.futures import ThreadPoolExecutor
from uuid import UUID, uuid4
import pytest
from sqlalchemy import text
from sqlalchemy.exc import IntegrityError
from vatican_backend.application.errors import ApplicationError
from vatican_backend.application.profiles import ProfileUseCases
from vatican_backend.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory
from .test_energy_lifecycle import energy_game, services
from vatican_backend.domain.profiles import avatar_catalog

HEADERS={'Authorization':'Bearer test-token'}


def test_profile_reads_and_updates_name_avatar_and_preferences_then_reloads(backend):
    client,_,_=backend
    original=client.get('/v1/profile',headers=HEADERS).json()
    assert original['avatarAssetKey'] is None and original['locale']=='ru' and original['displayTimezoneIana']=='UTC'
    changed=client.put('/v1/profile',headers=HEADERS,json={'expectedVersion':original['version'],
        'displayName':'  Рыцарь  ','avatarAssetKey':'avatars/knight','locale':'en-US','displayTimezoneIana':'Europe/Moscow'})
    assert changed.status_code==200
    value=changed.json()
    assert value=={**original,'version':original['version']+1,'displayName':'Рыцарь','avatarAssetKey':'avatars/knight',
                  'locale':'en-US','displayTimezoneIana':'Europe/Moscow'}
    use_case=ProfileUseCases(PostgresUnitOfWorkFactory(client.app.state.database))
    assert use_case.get(UUID(value['accountId']))==value
    # Old rename callers preserve the avatar and preferences.
    renamed=client.put('/v1/profile',headers=HEADERS,json={'expectedVersion':value['version'],'displayName':'Новое имя'}).json()
    assert renamed['avatarAssetKey']==value['avatarAssetKey'] and renamed['locale']=='en-US'
    cleared=client.patch('/v1/profile',headers=HEADERS,json={'expectedVersion':renamed['version'],'avatarAssetKey':None})
    assert cleared.status_code==200 and cleared.json()['avatarAssetKey'] is None
    assert cleared.json()['displayName']=='Новое имя'


@pytest.mark.parametrize('changes',[
    {},{'displayName':None},{'displayName':42},{'displayName':'   '},{'displayName':'a'*33},
    {'displayName':'x\ny'},{'displayName':'x\x00y'},{'displayName':'x\u200by'},{'displayName':'\ud800'},
    {'displayName':'😀'*31},{'avatarAssetKey':''},{'avatarAssetKey':'avatars/unknown'},{'avatarAssetKey':'../other.png'},
    {'avatarAssetKey':'avatars/../other.png'},{'avatarAssetKey':'avatars//other.png'},
    {'avatarAssetKey':'https://example.com/avatar.png'},{'avatarAssetKey':'/etc/passwd'},
    {'avatarAssetKey':'a'*129},{'locale':None},{'locale':'unknown-locale'},
    {'displayTimezoneIana':None},{'displayTimezoneIana':'Europe/Unknown'},
    {'displayTimezoneIana':'../../etc/passwd'},{'accountId':str(uuid4())},{'currentEnergy':999},
    {'version':999},{'permanentCurrency':999}
])
def test_profile_rejects_invalid_fields_without_mutating_any_state(backend,changes):
    client,_,_=backend;before=client.get('/v1/profile',headers=HEADERS).json()
    # Use cases also reject values that cannot be represented in a JSON request.
    account=UUID(before['accountId'])
    with pytest.raises(ApplicationError) as error:
        client.app.state.use_cases.profiles.update(account,before['version'],changes)
    assert error.value.status_code==422
    assert client.get('/v1/profile',headers=HEADERS).json()==before


@pytest.mark.parametrize('version',[True,0,-1,'1',2_147_483_647])
def test_profile_version_is_strict_and_cannot_overflow(backend,version):
    client,_,_=backend
    result=client.patch('/v1/profile',headers=HEADERS,json={'expectedVersion':version,'displayName':'Name'})
    assert result.status_code==422


def test_profile_http_requires_auth_and_forbids_foreign_owner_or_protected_fields(backend):
    client,_,_=backend
    assert client.get('/v1/profile').status_code==401
    assert client.patch('/v1/profile',json={'expectedVersion':1,'avatarAssetKey':None}).status_code==401
    for field in ('accountId','totalXp','currentEnergy','roles'):
        result=client.patch('/v1/profile',headers=HEADERS,json={'expectedVersion':1,field:'private-input'})
        assert result.status_code==422 and 'private-input' not in result.text


def test_concurrent_name_and_avatar_changes_share_one_version(backend):
    client,_,_=backend;before=client.get('/v1/profile',headers=HEADERS).json()
    api=client.app.state.use_cases.profiles;account=UUID(before['accountId'])
    def update(changes):
        try:return api.update(account,before['version'],changes)
        except ApplicationError as error:return error.status_code
    with ThreadPoolExecutor(max_workers=2) as pool:
        results=list(pool.map(update,[{'displayName':'Winner'},{'avatarAssetKey':'avatars/guard'}]))
    assert sum(isinstance(r,dict) for r in results)==1 and 409 in results
    saved=api.get(account)
    assert saved==next(r for r in results if isinstance(r,dict)) and saved['version']==before['version']+1


def test_preference_write_failure_rolls_back_name_and_profile_version(backend):
    client,_,_=backend;api=client.app.state.use_cases.profiles
    before=client.get('/v1/profile',headers=HEADERS).json();db=client.app.state.database
    with db.engine.begin() as connection:
        connection.execute(text('ALTER TABLE account_preferences ADD CONSTRAINT injected_avatar_failure CHECK (avatar_asset_key IS NULL)'))
    try:
        with pytest.raises(IntegrityError):api.update(UUID(before['accountId']),before['version'],
            {'displayName':'Must roll back','avatarAssetKey':'avatars/knight'})
        assert api.get(UUID(before['accountId']))==before
    finally:
        with db.engine.begin() as connection:
            connection.execute(text('ALTER TABLE account_preferences DROP CONSTRAINT injected_avatar_failure'))


def test_preferences_do_not_change_utc_energy_calculation(energy_game):
    client,_,_,account,clock=energy_game
    api=services(energy_game);initial=api.energy.get(account)
    profile=api.profiles.get(account)
    api.profiles.update(account,profile['version'],{'displayTimezoneIana':'America/New_York','locale':'en-US',
                                                  'avatarAssetKey':'avatars/knight'})
    assert api.energy.get(account)==initial


def test_unicode_name_is_normalized_and_ensure_does_not_reset_preferences(backend):
    client,_,_=backend;api=client.app.state.use_cases.profiles
    profile=client.get('/v1/profile',headers=HEADERS).json();account=UUID(profile['accountId'])
    saved=api.update(account,profile['version'],{'displayName':'e\u0301','avatarAssetKey':'avatars/knight'})
    assert saved['displayName']=='é'
    assert api.ensure(account)==saved


def test_avatar_catalog_is_authenticated_and_retired_avatar_is_preserved_on_rename(backend):
    client,_,_=backend;api=client.app.state.use_cases.profiles
    assert client.get('/v1/profile/avatars').status_code==401
    options=client.get('/v1/profile/avatars',headers=HEADERS).json()['avatars']
    assert len(options)==3 and len({o['assetKey'] for o in options})==3
    profile=client.get('/v1/profile',headers=HEADERS).json()
    with client.app.state.database.engine.begin() as connection:
        connection.execute(text('UPDATE account_preferences SET avatar_asset_key=:key WHERE account_id=:account'),
                           {'key':'avatars/retired','account':UUID(profile['accountId'])})
    saved=api.rename(UUID(profile['accountId']),profile['version'],'Legacy avatar')
    assert saved['avatarAssetKey']=='avatars/retired'


@pytest.mark.parametrize('changes',[{'assetKey':None},{'symbol':''},{'symbol':'\n'},{'backgroundColor':'red'},{'title':''}])
def test_bad_avatar_configuration_is_rejected(changes):
    with pytest.raises(ValueError):avatar_catalog([{'assetKey':'avatars/guard','title':'Страж','symbol':'С',
                                                   'backgroundColor':'#123456',**changes}])


def test_profile_change_does_not_rewrite_or_charge_an_existing_run(energy_game):
    from .test_energy_lifecycle import reserve,ack,balance
    client,_,slot,account,_=energy_game;api=services(energy_game)
    before=api.profiles.get(account);pending=reserve(energy_game)
    api.profiles.update(account,before['version'],{'displayName':'New name','avatarAssetKey':'avatars/guard'})
    assigned=api.runs.server_assignment(slot.server_id,slot.secret)
    assert assigned['displayName']==before['displayName']
    assert assigned['profileVersion']==before['version']
    assert balance(energy_game)==95
    ack(energy_game,pending)
    grant=api.runs.assignment_status(UUID(pending['runId']),account)
    assert grant['profileDisplayName']==before['displayName'] and grant['profileVersion']==before['version']
    assert api.profiles.get(account)['displayName']=='New name' and balance(energy_game)==95
