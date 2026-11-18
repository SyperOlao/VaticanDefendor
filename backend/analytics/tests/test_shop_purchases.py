from contextlib import contextmanager
from datetime import datetime, timedelta, timezone
import os
from uuid import UUID, uuid4, uuid5
import pytest
from sqlalchemy import text
from vatican_analytics.application.errors import ApplicationError
from vatican_analytics.application.use_cases import ProjectEvents
from vatican_analytics.domain.events import PURCHASE_EVENT, event_ids, receipt
from vatican_analytics.domain.projection import rows
from vatican_analytics.init_db import init_db
from vatican_analytics.persistence.clickhouse import Database
from vatican_analytics.persistence.clickhouse.repositories.projection import ClickHouseProjection
from vatican_analytics.persistence.postgres.tables import AnalyticsInbox


def payload(account=None,operation=None):
    account,operation=account or uuid4(),operation or uuid4()
    return {'purchaseId':str(uuid5(account,'shop.purchase:'+str(operation))),'accountId':str(account),'operationId':str(operation),
        'offerId':'sword','catalogVersion':'v1','currencyId':'PRESTIGE','chargedAmount':25,'weaponConfigurationId':'sword.rare',
        'weaponInstanceIds':[str(uuid4())],'status':'Completed','completedAt':datetime.now(timezone.utc).isoformat()}


@pytest.mark.parametrize('patch',[{'status':'Pending'},{'status':'Rejected'},{'currencyId':'USD'},
    {'chargedAmount':-1},{'chargedAmount':True},{'chargedAmount':2**63},{'weaponInstanceIds':[]},
    {'weaponInstanceIds':[str(uuid4()),str(uuid4())]},{'weaponInstanceIds':['not-a-uuid']},
    {'purchaseId':str(uuid4())},{'accountId':str(uuid4())},{'token':'private'},
    {'completedAt':'2026-11-17T12:00:00'},{'catalogVersion':'bad/version'}])
def test_invalid_or_unconfirmed_purchase_never_enters_inbox(analytics_actor,patch):
    with pytest.raises(ApplicationError) as error:analytics_actor.api.receive(uuid4(),PURCHASE_EVENT,1,{**payload(),**patch})
    assert error.value.status_code==422


def test_confirmed_virtual_currency_purchase_has_immutable_source_and_distinct_fact(analytics_actor):
    actor=analytics_actor;body=payload();identity=uuid4()
    assert actor.api.receive(identity,PURCHASE_EVENT,1,body)['status']=='PENDING'
    assert actor.worker.once()
    saved=actor.api.receive(identity,PURCHASE_EVENT,1,body)['receipt']
    assert saved==receipt(body,PURCHASE_EVENT)
    assert actor.api.receive(uuid4(),PURCHASE_EVENT,1,body)['receipt']==saved and not actor.worker.once()
    for patch in ({'chargedAmount':26},{'offerId':'other'},{'catalogVersion':'v2'},
                  {'weaponInstanceIds':[str(uuid4())]},{'weaponConfigurationId':'sword.common'}):
        with pytest.raises(ApplicationError) as error:actor.api.receive(uuid4(),PURCHASE_EVENT,1,{**body,**patch})
        assert error.value.status_code==409
    projected=rows(body)
    assert projected['raw_game_events'][0]['source']=='shop' and 'fact_purchases' not in projected
    assert projected['fact_shop_purchases'][0]['charged_amount']==25
    assert len(actor.projection.tables['fact_shop_purchases'])==1


def test_purchase_operation_uuid_is_scoped_by_account_and_alias_cannot_be_rebound(analytics_actor):
    actor=analytics_actor;operation=uuid4();first=payload(operation=operation);second=payload(operation=operation)
    message=uuid4();actor.api.receive(message,PURCHASE_EVENT,1,first)
    with pytest.raises(ApplicationError) as error:actor.api.receive(message,PURCHASE_EVENT,1,second)
    assert error.value.status_code==409
    actor.api.receive(uuid4(),PURCHASE_EVENT,1,second)
    actor.worker.once();actor.worker.once()
    assert first['purchaseId']!=second['purchaseId'] and len(actor.projection.tables['fact_shop_purchases'])==2




@pytest.mark.parametrize('fail_after',[1,2])
def test_live_clickhouse_purchase_partial_write_and_lost_ack_do_not_double_count(analytics_actor,fail_after):
    url=os.getenv('VD_TEST_ANALYTICS_DATABASE_URL')
    if not url:pytest.skip('Set VD_TEST_ANALYTICS_DATABASE_URL for live ClickHouse')
    init_db(url);warehouse=Database(url);actor=analytics_actor;body=payload();source=UUID(body['purchaseId']);identity=uuid4()
    try:
        class FaultyWarehouse:
            class Engine:
                @contextmanager
                def connect(self):
                    with warehouse.engine.connect() as connection:
                        class FaultyConnection:
                            count=0
                            def execute(self,statement,values):
                                connection.execute(statement,values);self.count+=1
                                if self.count==fail_after:raise RuntimeError('Purchase warehouse acknowledgement lost')
                        yield FaultyConnection()
            engine=Engine()
        actor.api.receive(identity,PURCHASE_EVENT,1,body)
        with pytest.raises(RuntimeError):ProjectEvents(actor.uow,ClickHouseProjection(FaultyWarehouse())).once()
        assert actor.api.receive(identity,PURCHASE_EVENT,1,body)['status']=='PENDING'
        with actor.database.transaction() as session:
            session.get(AnalyticsInbox,(PURCHASE_EVENT,source)).available_at=datetime.now(timezone.utc)-timedelta(seconds=1)
        assert ProjectEvents(actor.uow,ClickHouseProjection(warehouse)).once()
        assert actor.api.receive(identity,PURCHASE_EVENT,1,body)['status']=='APPLIED'
        assert not ProjectEvents(actor.uow,ClickHouseProjection(warehouse)).once()
        with warehouse.engine.connect() as connection:
            assert connection.exec_driver_sql('SELECT count(),sum(charged_amount) FROM fact_shop_purchases FINAL WHERE purchase_id=%(id)s',{'id':str(source)}).one()==(1,25)
            assert connection.exec_driver_sql('SELECT count() FROM raw_game_events FINAL WHERE event_id=%(id)s',{'id':event_ids(body)[0]}).scalar()==1
    finally:warehouse.close()
