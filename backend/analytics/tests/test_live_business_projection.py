from contextlib import contextmanager
from datetime import datetime, timedelta, timezone
import os
from uuid import UUID, uuid4
import pytest
from vatican_analytics.application.use_cases import ProjectEvents
from vatican_analytics.domain.events import GIFT_EVENT, CASE_EVENT, event_ids
from vatican_analytics.init_db import init_db
from vatican_analytics.persistence.clickhouse import Database
from vatican_analytics.persistence.clickhouse.repositories.projection import ClickHouseProjection
from vatican_analytics.persistence.postgres.tables import AnalyticsInbox
from .test_business_events import gift_payload, case_payload


@pytest.mark.parametrize('event,factory,field,table,key',[(GIFT_EVENT,gift_payload,'claimId','fact_level_gift_claims','claim_id'),
    (CASE_EVENT,case_payload,'openingId','fact_case_settlements','opening_id')])
@pytest.mark.parametrize('fail_after',[1,2])
def test_real_clickhouse_business_partial_writes_and_lost_ack_are_once_only(analytics_actor,event,factory,field,table,key,fail_after):
    url=os.getenv('VD_TEST_ANALYTICS_DATABASE_URL')
    if not url:pytest.skip('Set VD_TEST_ANALYTICS_DATABASE_URL for live ClickHouse')
    init_db(url);warehouse=Database(url);actor=analytics_actor;body=factory();source=UUID(body[field]);message=uuid4()
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
                                if self.count==fail_after:raise RuntimeError('Business projection acknowledgement lost')
                        yield FaultyConnection()
            engine=Engine()
        actor.api.receive(message,event,1,body)
        with pytest.raises(RuntimeError):ProjectEvents(actor.uow,ClickHouseProjection(FaultyWarehouse())).once()
        assert actor.api.receive(message,event,1,body)['status']=='PENDING'
        with actor.database.transaction() as session:
            session.get(AnalyticsInbox,(event,source)).available_at=datetime.now(timezone.utc)-timedelta(seconds=1)
        assert ProjectEvents(actor.uow,ClickHouseProjection(warehouse)).once()
        result=actor.api.receive(message,event,1,body)
        assert result['status']=='APPLIED' and result['receipt']['eventIds']==event_ids(body,event)
        assert actor.api.receive(uuid4(),event,1,body)['receipt']==result['receipt']
        with warehouse.engine.connect() as connection:
            assert connection.exec_driver_sql('SELECT count() FROM raw_game_events FINAL WHERE event_id=%(id)s',{'id':event_ids(body,event)[0]}).scalar()==1
            assert connection.exec_driver_sql(f'SELECT count() FROM {table} FINAL WHERE {key}=%(id)s',{'id':str(source)}).scalar()==1
    finally:warehouse.close()
