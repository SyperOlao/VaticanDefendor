from copy import deepcopy
from uuid import UUID, uuid4

from fastapi.testclient import TestClient
from pydantic import TypeAdapter

from vatican_wallet.application.catalog import EventName
from vatican_wallet.bootstrap.settings import Settings
from vatican_wallet.presentation.app import create_app
from vatican_wallet.presentation.message_examples import WalletCommand


def test_all_command_examples_have_typed_payloads_and_idempotent_schema_generation():
    with TestClient(create_app(Settings('postgresql://localhost:1/unused','s'*40))) as client:
        first=client.get('/openapi.json').json()
        assert client.get('/openapi.json').json()==first
        command=first['paths']['/internal/messages']['post']
        media=command['requestBody']['content']['application/json']
        assert set(media['examples'])==set(EventName)
        assert media['schema']['discriminator']['propertyName']=='eventName'
        assert len({s['value']['messageId'] for s in media['examples'].values()})==len(EventName)
        for sample in media['examples'].values():
            TypeAdapter(WalletCommand).validate_python(sample['value'])
        assert 'receipt.outcome' in command['description']
        for path in ('/docs','/redoc'):
            assert client.get(path).status_code==200


def test_swagger_commands_execute_and_replay_without_double_spending(wallet_storage):
    class Auth:
        def authenticate(self,token):
            return type('Principal',(),{'account_id':account})()
    settings=Settings(wallet_storage[1],'s'*40)
    headers={'X-Service-Secret':settings.service_secret}
    with TestClient(create_app(settings,auth_gateway=Auth())) as client:
        spec=client.get('/openapi.json').json()
        samples=spec['paths']['/internal/messages']['post']['requestBody']['content']['application/json']['examples']
        account=UUID(next(iter(samples.values()))['value']['payload']['accountId'])
        received={}
        for event,sample in samples.items():
            body=sample['value']
            response=client.post('/internal/messages',json=body,headers=headers)
            assert response.status_code==200,(event,response.text)
            result=response.json()
            assert result['status']=='APPLIED'
            received[event]=result
        for event,sample in samples.items():
            assert client.post('/internal/messages',json=sample['value'],headers=headers).json()==received[event]
        result=client.get('/v1/wallet',headers={'Authorization':'Bearer test-only'}).json()
        balances={b['currencyId']:b['balance'] for b in result['balances']}
        assert balances=={'PRESTIGE':520,'GEMS':10}
        refused=deepcopy(samples[EventName.PURCHASE_CHARGE]['value'])
        refused['messageId']=str(uuid4());refused['payload']['operationId']=str(uuid4())
        refused['payload']['amount']=999
        response=client.post('/internal/messages',json=refused,headers=headers)
        assert response.status_code==200
        assert response.json()['status']=='APPLIED'
        assert response.json()['receipt']['status']=='REJECTED'
        assert response.json()['receipt']['code']=='InsufficientFunds'
        assert client.post('/internal/messages',json=refused,headers=headers).json()==response.json()
