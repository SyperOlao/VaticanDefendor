"""Mode-aware run.analytics v1, preserving legacy dedicated envelopes and facts."""
from copy import deepcopy
from uuid import UUID,uuid4
import pytest
from vatican_analytics.application.errors import ApplicationError
from vatican_analytics.domain.events import EVENT,parse,digest,receipt
from vatican_analytics.domain.projection import rows
from .test_ingestion import payload
from .test_live_daily_reports import warehouse


@pytest.mark.parametrize('mode',['LOCAL','DEDICATED'])
def test_run_mode_flows_to_facts_and_stable_receipts(analytics_actor,mode):
    actor=analytics_actor;body=payload(simulationMode=mode)
    assert parse(body)==body
    assert actor.api.receive(uuid4(),EVENT,1,body)['status']=='PENDING'
    assert actor.worker.once()
    result=actor.api.receive(uuid4(),EVENT,1,body)
    assert result['status']=='APPLIED' and result['receipt']==receipt(body)
    assert actor.projection.tables['fact_run_settlements'][UUID(body['runId'])]['simulation_mode']==mode
    assert next(iter(actor.projection.tables['fact_xp_transactions'].values()))['source_type']==mode+'_RUN'
    assert len(actor.projection.tables['raw_game_events'])==3
    assert not actor.worker.once()


@pytest.mark.parametrize('mode',[None,True,1,'local','REMOTE',{},[]])
def test_explicit_unknown_mode_is_rejected_before_inbox_write(analytics_actor,mode):
    with pytest.raises(ApplicationError) as error:analytics_actor.api.receive(uuid4(),EVENT,1,payload(simulationMode=mode))
    assert error.value.status_code==422


def test_absent_mode_preserves_legacy_hash_receipt_and_dedicated_projection(analytics_actor):
    import hashlib,json
    body=payload();original=deepcopy(body)
    expected=hashlib.sha256(json.dumps([EVENT,1,body],sort_keys=True,separators=(',',':')).encode()).digest()
    actor=analytics_actor
    actor.api.receive(uuid4(),EVENT,1,body);actor.worker.once()
    assert digest(body)==expected and body==original and 'simulationMode' not in body
    assert actor.api.receive(uuid4(),EVENT,1,body)['receipt']['payloadHash']==expected.hex()
    assert rows(body)['fact_run_settlements'][0]['simulation_mode']=='DEDICATED'
    assert rows(body)['fact_xp_transactions'][0]['source_type']=='DEDICATED_RUN'
    # Adding an explicit mode changes an immutable source, even if semantically dedicated.
    with pytest.raises(ApplicationError) as error:actor.api.receive(uuid4(),EVENT,1,{**body,'simulationMode':'DEDICATED'})
    assert error.value.status_code==409


def test_existing_clickhouse_table_gets_mode_default_without_losing_rows(warehouse):
    from vatican_analytics.init_db import create_schema
    from vatican_analytics.persistence.clickhouse import Base
    body=payload();legacy=rows(body)['fact_run_settlements'][0]
    legacy.pop('simulation_mode')
    with warehouse.engine.connect() as connection:
        connection.exec_driver_sql('ALTER TABLE fact_run_settlements DROP COLUMN simulation_mode')
        connection.execute(Base.metadata.tables['fact_run_settlements'].insert(),[
            {k:str(v) if isinstance(v,UUID) else v for k,v in legacy.items()}])
        create_schema(connection);create_schema(connection)
        stored=connection.exec_driver_sql('SELECT simulation_mode,xp_granted FROM fact_run_settlements FINAL WHERE run_id=%(run)s',
            {'run':body['runId']}).one()
        assert stored==('DEDICATED',80)
        assert connection.exec_driver_sql('SELECT count() FROM fact_run_settlements FINAL').scalar()==1
