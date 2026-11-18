from contextlib import contextmanager
from datetime import datetime, timedelta, timezone
import os
from uuid import UUID
import pytest
from sqlalchemy import select
from vatican_analytics.application.use_cases import ProjectEvents
from vatican_analytics.domain.events import EVENT
from vatican_analytics.init_db import init_db
from vatican_analytics.persistence.clickhouse import Database
from vatican_analytics.persistence.clickhouse.repositories.projection import ClickHouseProjection
from vatican_analytics.persistence.postgres.tables import AnalyticsInbox
from .test_ingestion import payload


@pytest.mark.parametrize('fail_after',[1,4])
@pytest.mark.parametrize('mode',['DEDICATED','LOCAL'])
def test_real_clickhouse_partial_write_and_lost_ack_are_logically_once_only(analytics_actor,fail_after,mode):
    url=os.getenv('VD_TEST_ANALYTICS_DATABASE_URL')
    if not url:pytest.skip('Set VD_TEST_ANALYTICS_DATABASE_URL for the live ClickHouse test')
    init_db(url);init_db(url)
    warehouse=Database(url);actor=analytics_actor;body=payload();run=UUID(body['runId'])
    if mode=='LOCAL':body['simulationMode']=mode
    try:
        class FaultyWarehouse:
            class Engine:
                @contextmanager
                def connect(self):
                    assert actor.database.engine.pool.checkedout()==0
                    with warehouse.engine.connect() as connection:
                        class FaultyConnection:
                            count=0
                            def execute(self,statement,values):
                                connection.execute(statement,values)
                                self.count+=1
                                if self.count==fail_after:raise RuntimeError('Warehouse acknowledgement lost after a partial insert')
                        yield FaultyConnection()
            engine=Engine()
        first=actor.api.receive(run,EVENT,1,body)
        assert first['status']=='PENDING'
        with pytest.raises(RuntimeError):ProjectEvents(actor.uow,ClickHouseProjection(FaultyWarehouse())).once()
        assert actor.api.receive(run,EVENT,1,body)['status']=='PENDING'
        with actor.database.transaction() as session:
            session.get(AnalyticsInbox,('run.analytics',run)).available_at=datetime.now(timezone.utc)-timedelta(seconds=1)
        assert ProjectEvents(actor.uow,ClickHouseProjection(warehouse)).once()
        assert actor.api.receive(run,EVENT,1,body)['status']=='APPLIED'
        assert not ProjectEvents(actor.uow,ClickHouseProjection(warehouse)).once()
        with warehouse.engine.connect() as connection:
            args={'run':str(run)}
            assert connection.exec_driver_sql('SELECT count() FROM raw_game_events WHERE run_id=%(run)s',args).scalar()>=3
            assert connection.exec_driver_sql('SELECT count() FROM raw_game_events FINAL WHERE run_id=%(run)s',args).scalar()==3
            assert connection.exec_driver_sql('SELECT count() FROM fact_run_settlements FINAL WHERE run_id=%(run)s',args).scalar()==1
            assert connection.exec_driver_sql('SELECT sum(xp_delta) FROM fact_xp_transactions FINAL WHERE run_id=%(run)s',args).scalar()==80
            assert connection.exec_driver_sql('SELECT count() FROM fact_player_level_ups FINAL WHERE run_id=%(run)s',args).scalar()==1
            assert connection.exec_driver_sql('SELECT simulation_mode FROM fact_run_settlements FINAL WHERE run_id=%(run)s',args).scalar()==mode
            assert connection.exec_driver_sql('SELECT source_type FROM fact_xp_transactions FINAL WHERE run_id=%(run)s',args).scalar()==mode+'_RUN'
    finally:warehouse.close()
