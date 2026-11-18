from concurrent.futures import ThreadPoolExecutor
from uuid import UUID, uuid4
import httpx
import pytest
from sqlalchemy import func, select
from vatican_backend.application.errors import ApplicationError
from vatican_backend.application.access import AuthenticatePlayer
from vatican_backend.application.ports import Principal
from vatican_backend.integrations.http import AuthHttpGateway
from vatican_backend.persistence.postgres.tables import Profile, AccountPreference
from vatican_backend.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory


@pytest.mark.parametrize('status,body,expected', [(401,{},401),(403,{},401),(500,{},503),(200,{},503),
    (200,{'accountId':'invalid','isGuest':False},503),(200,{'accountId':str(uuid4()),'isGuest':'false'},503)])
def test_invalid_auth_contract_fails_closed(status, body, expected):
    with httpx.Client(transport=httpx.MockTransport(lambda request: httpx.Response(status,json=body))) as client:
        gateway = AuthHttpGateway(client, 'http://auth')
        with pytest.raises(ApplicationError) as error:
            gateway.authenticate('opaque-secret')
        assert error.value.status_code == expected


def test_auth_resolves_uuid_from_the_authority_and_never_decodes_opaque_token():
    account_id = uuid4()
    def handle(request):
        assert request.url.path == '/v1/auth/me'
        assert request.headers['authorization'] == 'Bearer opaque-secret'
        return httpx.Response(200,json={'accountId':str(account_id),'isGuest':True})
    with httpx.Client(transport=httpx.MockTransport(handle)) as client:
        assert AuthHttpGateway(client,'http://auth').authenticate('opaque-secret') == Principal(account_id,True)


def test_concurrent_first_authentication_creates_one_profile_outside_auth_call(storage):
    database,_ = storage
    account_id = uuid4()
    class Auth:
        def authenticate(self, token):
            assert database.engine.pool.checkedout() == 0
            return Principal(account_id,False)
    # Synchronize external verification so assertions do not race the other thread's game transaction.
    from threading import Barrier
    barrier = Barrier(2)
    class ConcurrentAuth:
        def authenticate(self, token):
            barrier.wait(timeout=5)
            return Principal(account_id,False)
    access=AuthenticatePlayer(Auth(),PostgresUnitOfWorkFactory(database))
    access.execute('Bearer example')
    account_id=uuid4()
    access=AuthenticatePlayer(ConcurrentAuth(),PostgresUnitOfWorkFactory(database))
    with ThreadPoolExecutor(max_workers=2) as pool:
        assert all(p.account_id == account_id for p in pool.map(access.execute,['Bearer example']*2))
    with database.transaction() as session:
        assert session.scalar(select(func.count()).select_from(Profile).where(Profile.account_id==account_id))==1
        assert session.get(AccountPreference,account_id) is not None


def test_errors_are_uniform_and_do_not_echo_invalid_fields(backend):
    client,_,_=backend
    response=client.put('/v1/profile',headers={'Authorization':'Bearer test-token'},
                        json={'expectedVersion':1,'displayName':'ok','secret-token-do-not-return':'sensitive'})
    assert response.status_code==422
    assert set(response.json())=={'code','detail','requestId','errors'}
    assert response.json()['requestId']==response.headers['x-request-id']
    assert 'secret-token-do-not-return' not in response.text
    assert 'sensitive' not in response.text
    assert set(client.get('/unknown').json())=={'code','detail','requestId','errors'}
