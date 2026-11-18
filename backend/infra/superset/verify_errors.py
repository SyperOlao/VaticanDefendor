"""Isolated failure probes; never stop services or modify business facts."""
from datetime import datetime, timedelta, timezone
import json
import os
from uuid import uuid4

import requests

from serving import health_select, identifier
from verify import client


def verify_health():
    from catalog import BUSINESS_DATASETS
    admin = client()
    scratch = identifier('vatican_bi_error_check_' + uuid4().hex)
    try:
        admin.command('CREATE DATABASE ' + scratch)
        admin.command(f'CREATE TABLE {scratch}.jobs ENGINE=Memory AS SELECT * FROM system.view_refreshes LIMIT 0')
        now = datetime.now(timezone.utc)
        cases = [('Scheduled',now,'','ok'), ('Scheduled',now,'Code: 60. private SQL sentinel','refresh_failed'),
                 ('Disabled',now,'','refresh_stopped'), ('WaitingForDependencies',now,'','dependency_wait'),
                 ('Scheduled',None,'','never_succeeded'), ('Scheduled',now-timedelta(minutes=11),'','stale')]
        rows = [['refresh_'+spec.name, state, at, now, now+timedelta(minutes=5), error]
                for spec,(state,at,error,_) in zip(BUSINESS_DATASETS,cases)]
        admin.insert(scratch.strip('`')+'.jobs', rows,
                     column_names=['view','status','last_success_time','last_refresh_time','next_refresh_time','exception'])
        for use_nulls in (0,1):
            result = list(admin.query(health_select(f'SELECT * FROM {scratch}.jobs'),
                                     settings={'join_use_nulls':use_nulls}).named_results())
            assert len(result)==7
            actual = {r['dataset']:r for r in result}
            for spec,(_,_,_,code) in zip(BUSINESS_DATASETS,cases):
                assert actual[spec.label]['health_code']==code, actual[spec.label]
            missing = actual[BUSINESS_DATASETS[-1].label]
            assert missing['health_code']=='missing_job'
            assert missing['last_success'] is None and missing['age_seconds'] is None
            assert 'private SQL sentinel' not in str(result)
        admin.command(f'TRUNCATE TABLE {scratch}.jobs')
        rows[0][1]='FutureState'
        rows[1][2]=None
        admin.insert(scratch.strip('`')+'.jobs', rows,
                     column_names=['view','status','last_success_time','last_refresh_time','next_refresh_time','exception'])
        result={r['dataset']:r for r in admin.query(health_select(f'SELECT * FROM {scratch}.jobs')).named_results()}
        assert result[BUSINESS_DATASETS[0].label]['health_code']=='unknown_state'
        assert result[BUSINESS_DATASETS[1].label]['health_code']=='refresh_failed'
        print('PASS: missing, failed, stopped, dependency wait, first refresh, stale, unknown, healthy; both JOIN modes; private SQL hidden')
    finally:
        try:
            admin.command('DROP DATABASE IF EXISTS '+scratch+' SYNC')
        finally:
            admin.close()


def verify_chart_errors():
    from superset.app import create_app
    from presentation import identity

    app = create_app()
    dataset_id = None
    probe_database_id = None
    base = os.getenv('SUPERSET_VERIFY_URL','http://superset:8088')
    with app.app_context():
        from superset import db
        from superset.connectors.sqla.models import SqlaTable
        from superset.models.core import Database
        try:
            database = db.session.query(Database).filter_by(uuid=identity('database','vatican-bi')).one()
            dataset = SqlaTable(table_name='bi_error_probe_'+uuid4().hex, database=database,
                               sql='SELECT * FROM bi_error_probe_missing_'+uuid4().hex)
            db.session.add(dataset)
            db.session.commit()
            dataset_id = dataset.id
            with requests.Session() as session:
                login = session.post(base+'/api/v1/security/login', json={
                    'username':os.getenv('VD_SUPERSET_ADMIN_USERNAME','admin'),
                    'password':os.environ['VD_SUPERSET_ADMIN_PASSWORD'],'provider':'db','refresh':True},timeout=30)
                login.raise_for_status()
                session.headers['Authorization']='Bearer '+login.json()['access_token']
                csrf=session.get(base+'/api/v1/security/csrf_token/',timeout=30);csrf.raise_for_status()
                session.headers['X-CSRFToken']=csrf.json()['result']
                context = {'datasource':{'id':dataset_id,'type':'table'}, 'force':True,
                    'queries':[{'columns':[], 'metrics':[{'expressionType':'SQL','sqlExpression':'count(*)','label':'probe'}],
                                'row_limit':1,'time_range':'No filter'}], 'result_format':'json','result_type':'full'}
                response = session.post(base+'/api/v1/chart/data',json=context,timeout=45)
                assert response.status_code>=400, response.status_code
                payload = response.json()
                assert 'schema_unavailable' in json.dumps(payload), payload
                assert response.headers['X-Request-ID'] in response.text
                assert response.headers['Cache-Control']=='no-store'
                assert 'SELECT ' not in response.text and 'bi_error_probe_missing_' not in response.text
                print('PASS: real missing-source HTTP error, actionable Russian message, request ID, no SQL, no false empty data')
                # A dedicated unusable connection exercises a real refused socket without
                # stopping the shared ClickHouse or touching its credentials.
                probe_database=Database(database_name='BI failure probe '+uuid4().hex,
                    sqlalchemy_uri='clickhousedb://probe:unused@127.0.0.1:1/default',
                    extra=json.dumps({'engine_params':{'connect_args':{'connect_timeout':1,'send_receive_timeout':2}}}))
                db.session.add(probe_database)
                db.session.flush()
                probe_database_id=probe_database.id
                dataset.database=probe_database
                dataset.sql='SELECT 1 AS probe'
                db.session.commit()
                response=session.post(base+'/api/v1/chart/data',json=context,timeout=45)
                assert response.status_code>=400
                assert 'dependency_unavailable' in response.text, response.json()
                assert all(secret not in response.text for secret in ('unused','127.0.0.1','SELECT '))
                assert response.headers['X-Request-ID'] in response.text
                print('PASS: real refused connection; retryable outage; connection details hidden; shared services unchanged')
        finally:
            db.session.rollback()
            if dataset_id is not None:
                dataset=db.session.get(SqlaTable,dataset_id)
                if dataset:
                    db.session.delete(dataset)
                    db.session.commit()
            if probe_database_id is not None:
                probe_database=db.session.get(Database,probe_database_id)
                if probe_database:
                    db.session.delete(probe_database)
                    db.session.commit()


if __name__ == '__main__':
    verify_health()
    verify_chart_errors()
