"""Documentation is executable: examples, HTTP statuses and security match the API."""
from uuid import uuid4
import hashlib
import pytest
from fastapi.testclient import TestClient
from pydantic import TypeAdapter
from vatican_backend.presentation import schemas
from vatican_backend.presentation.content import Publication
from vatican_backend.presentation.openapi import OPERATIONS,REQUEST_EXAMPLES
from vatican_backend.presentation import openapi_examples as ex
from vatican_backend.presentation.app import create_app
from vatican_backend.bootstrap.settings import Settings
from vatican_backend.application.weapons import WeaponDefinitionsUseCases


@pytest.fixture
def docs_client(monkeypatch):
    # OpenAPI/docs require no database. Avoid connecting during the app lifespan.
    monkeypatch.setattr(WeaponDefinitionsUseCases,'register_catalog',lambda self:None)
    settings=Settings('postgresql://docs@127.0.0.1:9/unreachable',{},120)
    with TestClient(create_app(settings)) as client:yield client


def test_every_operation_has_group_description_response_schema_and_valid_examples(docs_client):
    schema=docs_client.get('/openapi.json').json()
    ids=[]
    for path,methods in schema['paths'].items():
        for method,operation in methods.items():
            assert operation['tags'] and operation['summary'] and operation['description'],(method,path)
            ids.append(operation['operationId'])
            definition=OPERATIONS[(method.upper(),path)]
            for status,response in definition['responses'].items():
                actual=operation['responses'][str(status)]
                assert actual['content']['application/json']['schema']
                for sample in actual['content']['application/json']['examples'].values():
                    TypeAdapter(response['model']).validate_python(sample['value'])
            assert operation['responses']['500']['content']['application/json']['schema']['$ref'].endswith('/ErrorResponse')
    assert len(ids)==len(set(ids))
    assert 'HTTP 200' in schema['info']['description'] and 'requestKey' in schema['info']['description']
    assert schema['components']['schemas']['ErrorResponse']['required']==['code','detail','requestId','errors']


def test_request_examples_validate_without_weakening_strict_bodies(docs_client):
    body_models={
        ('POST','/v1/runs'):schemas.StartRun,('POST','/v1/local-runs'):schemas.StartLocalRun,
        ('POST','/v1/local-runs/{run_id}/finish'):schemas.FinishLocalRun,
        ('PUT','/v1/profile'):schemas.RenameProfile,('PATCH','/v1/profile'):schemas.UpdateProfile,
        ('POST','/v1/cases/{case_id}/open'):schemas.OpenCase,
        ('POST','/v1/level-rewards/{claim_id}/claim'):schemas.ClaimLevelReward,
        ('POST','/v1/prestige-upgrades/purchases'):schemas.BuyPrestigeUpgrade,
        ('PUT','/internal/content/{release_id}'):Publication}
    for suffix in ('ready','recover','ack','heartbeat','abandon','checkpoint','results'):
        body_models[('POST','/internal/servers/{server_id}/'+suffix)]=schemas.ServerReady if suffix in ('ready','recover') else schemas.CheckpointPublish if suffix=='checkpoint' else schemas.RunResult if suffix=='results' else schemas.AssignmentAck
    api=docs_client.get('/openapi.json').json()
    for (method,path),samples in REQUEST_EXAMPLES.items():
        cls=body_models[(method,path)]
        assert cls.model_config['extra']=='forbid'
        published=api['paths'][path][method.lower()]['requestBody']['content']['application/json']['examples']
        for sample in published.values():cls.model_validate(sample['value'])
    assert hashlib.sha256(ex.PUBLICATION['payloadJson'].encode()).hexdigest()==ex.PUBLICATION['payloadSha256']
    assert hashlib.sha256(ex.CHECKPOINT['snapshotJson'].encode()).hexdigest()==ex.CHECKPOINT['sha256']


def test_player_server_and_publisher_authorize_are_separate(docs_client):
    schema=docs_client.get('/openapi.json').json();paths=schema['paths'];schemes=schema['components']['securitySchemes']
    assert schemes['HTTPBearer']['scheme']=='bearer'
    assert schemes['DedicatedServerSecret']['name']=='X-Server-Secret'
    assert schemes['ContentPublisherSecret']['name']=='X-Content-Admin-Secret'
    assert paths['/v1/profile']['get']['security']==[{'HTTPBearer':[]}]
    assert paths['/internal/servers/{server_id}/ready']['post']['security']==[{'DedicatedServerSecret':[]}]
    assert paths['/internal/content/{release_id}']['put']['security']==[{'ContentPublisherSecret':[]}]
    assert 'security' not in paths['/v1/cases']['get']
    assert 'security' not in paths['/v1/reward-bundles/{bundle_id}']['get']
    assert docs_client.get('/v1/profile').status_code==401
    assert docs_client.post('/internal/servers/'+str(uuid4())+'/ready',json=ex.READY).status_code==401
    assert docs_client.put('/internal/content/'+ex.RELEASE,json=ex.PUBLICATION).status_code==401


def test_async_operations_show_202_and_local_rejection_is_a_200_body(docs_client):
    paths=docs_client.get('/openapi.json').json()['paths']
    for path in ('/v1/prestige-upgrades/purchases','/v1/cases/{case_id}/open','/v1/level-rewards/{claim_id}/claim'):
        assert {'200','202'}<=paths[path]['post']['responses'].keys()
    assert '202' in paths['/v1/runs']['post']['responses'] and '200' not in paths['/v1/runs']['post']['responses']
    local=paths['/v1/local-runs/{run_id}/finish']['post']['responses']['200']['content']['application/json']['examples']
    assert local['rejected']['value']['validationStatus']=='REJECTED'
    assert local['rejected']['value']['settlement'] is None
    reset=paths['/v1/profile']['patch']['requestBody']['content']['application/json']['examples']['resetAvatar']['value']
    assert 'avatarAssetKey' in reset and reset['avatarAssetKey'] is None
    assert paths['/v1/profile']['patch']['responses']['422']['content']['application/json']['schema']['$ref'].endswith('/ErrorResponse')


def test_swagger_and_redoc_render_with_search_and_sanitized_examples(docs_client):
    swagger=docs_client.get('/docs')
    assert swagger.status_code==200 and 'SwaggerUIBundle' in swagger.text
    assert '"filter": true' in swagger.text and '"persistAuthorization": false' in swagger.text
    assert docs_client.get('/redoc').status_code==200
    schema=docs_client.get('/openapi.json').text
    assert 'DEMO_CONNECTION_GRANT_NOT_VALID' in schema
    assert 'HTTPValidationError' not in docs_client.get('/openapi.json').json()['components']['schemas']
