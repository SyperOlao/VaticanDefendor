from copy import deepcopy
from uuid import UUID, uuid4

from dishka import Provider, Scope, provide
from fastapi.testclient import TestClient
from sqlalchemy import update

from vatican_analytics.application.ports import UnitOfWorkFactory
from vatican_analytics.bootstrap.di import create_container
from vatican_analytics.bootstrap.settings import Settings
from vatican_analytics.domain.events import EVENT_FIELDS, receipt
from vatican_analytics.persistence.postgres.tables import AnalyticsInbox
from vatican_analytics.presentation import app as app_module


def test_swagger_contains_report_values_missing_xp_and_all_source_types():
    settings=Settings('postgresql://localhost:1/unused','s'*40,'clickhouse+http://localhost:1/unused')
    with TestClient(app_module.create_app(settings)) as client:
        spec=client.get('/openapi.json').json()
        assert spec==client.get('/openapi.json').json()
        examples=spec['paths']['/internal/messages']['post']['requestBody']['content']['application/json']['examples']
        runs=[s['value']['payload'] for s in examples.values() if s['value']['eventName']=='run.analytics']
        assert any(r['progression'] is None for r in runs)
        assert any(r['progression'] and r['progression']['xpGranted']>0 for r in runs)
        report=spec['paths']['/internal/reports/daily']['get']
        assert 'сутки UTC' in report['description']
        samples=report['responses']['200']['content']['application/json']['examples']
        assert samples['activity']['value']['runs']['runsWithoutXp']==1
        assert samples['empty']['value']['runs']['completed']==0


def test_real_inbox_accepts_projects_and_replays_every_documented_example(analytics_actor,analytics_storage,monkeypatch):
    actor=analytics_actor
    class Overrides(Provider):
        @provide(scope=Scope.APP,override=True)
        def uow(self)->UnitOfWorkFactory:
            return actor.uow
    monkeypatch.setattr(app_module,'create_container',lambda settings:create_container(settings,Overrides()))
    settings=Settings(analytics_storage[1],'s'*40,'clickhouse+http://localhost:1/unused')
    headers={'X-Service-Secret':settings.service_secret}
    kinds={'run.analytics':'run','level-gift.analytics':'level-gift','case.analytics':'case',
           'shop.purchase.analytics':'shop-purchase','wallet.transaction.analytics':'wallet-transaction'}
    with TestClient(app_module.create_app(settings)) as client:
        spec=client.get('/openapi.json').json()
        examples=spec['paths']['/internal/messages']['post']['requestBody']['content']['application/json']['examples']
        for sample in examples.values():
            body=sample['value'];event=body['eventName'];source=body['payload'][EVENT_FIELDS[event]]
            accepted=client.post('/internal/messages',json=body,headers=headers)
            assert accepted.status_code==202,(event,accepted.text)
            assert accepted.json()['receipt'] is None
            assert actor.worker.once()
            applied=client.post('/internal/messages',json=body,headers=headers)
            assert applied.status_code==200,(event,applied.text)
            assert applied.json()['receipt']==receipt(body['payload'],event)
            state=client.get(f'/internal/sources/{kinds[event]}/{source}',headers=headers)
            assert state.status_code==200,state.text
            assert state.json()['status']=='COMPLETED'
            assert state.json()['receipt']==applied.json()['receipt']
            assert client.post('/internal/messages',json=body,headers=headers).json()==applied.json()
        review=deepcopy(examples['run.analytics']['value'])
        review['messageId']=str(uuid4());review['payload']['runId']=str(uuid4())
        assert client.post('/internal/messages',json=review,headers=headers).status_code==202
        with actor.database.transaction() as session:
            session.execute(update(AnalyticsInbox).where(AnalyticsInbox.event_name=='run.analytics',
                AnalyticsInbox.source_id==UUID(review['payload']['runId'])).values(status='REVIEW',attempts=20,last_error='ProjectionUnavailable'))
        response=client.post('/internal/messages',json=review,headers=headers)
        assert response.status_code==409 and response.json()['status']=='REVIEW'
        changed=deepcopy(review);changed['payload']['churchLevel']=2
        conflict=client.post('/internal/messages',json=changed,headers=headers)
        assert conflict.status_code==409 and conflict.json()['code']=='ResourceConflict'
