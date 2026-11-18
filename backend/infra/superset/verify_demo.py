"""Verify filled demo charts, independent KPI totals, filters and namespace isolation."""
from copy import deepcopy
from datetime import timedelta
import json
import math
import os

import psycopg2
import requests

from catalog import BOARDS, DATASETS
from demo import DEMO_SOURCE, DEMO_BI, demo_boards, scoped_identity, verify_demo_metrics
from demo_data import utc_reference
from demo_presentation import DISPLAY_COLUMNS, FILTER_SAMPLE
from presentation import identity
from serving import identifier
from verify import client


def main():
    admin = client()
    try:
        reference, expected_json = admin.query(f"SELECT reference_at, expected_json FROM {DEMO_SOURCE}.demo_seed_manifest FINAL WHERE seed_key='vatican-demo' AND state='ready'").first_row
        reference = utc_reference(reference)
        expected = json.loads(expected_json)
        verify_demo_metrics(admin, expected, reference)
        with psycopg2.connect(host=os.getenv('SUPERSET_METADATA_HOST', 'superset_metadata'), dbname='superset',
                user='superset', password=os.environ['SUPERSET_METADATA_PASSWORD']) as conn:
            with conn.cursor() as cursor:
                cursor.execute('SELECT id, uuid, query_context FROM slices')
                records = {str(uuid): (chart_id, json.loads(context)) for chart_id, uuid, context in cursor.fetchall() if context}
        original_sources = {records[str(identity('chart', chart.key))][1]['datasource']['id']
                            for board in BOARDS for row in board.rows for chart in row}
        base = os.getenv('VD_SUPERSET_VERIFY_URL', 'http://superset:8088').rstrip('/')
        charts = []
        with requests.Session() as session:
            login = session.post(base + '/api/v1/security/login', json={
                'username': os.getenv('VD_SUPERSET_ADMIN_USERNAME', 'admin'),
                'password': os.environ['VD_SUPERSET_ADMIN_PASSWORD'], 'provider': 'db', 'refresh':True}, timeout=30)
            login.raise_for_status()
            session.headers['Authorization'] = 'Bearer ' + login.json()['access_token']
            csrf = session.get(base + '/api/v1/security/csrf_token/', timeout=30)
            csrf.raise_for_status()
            session.headers['X-CSRFToken'] = csrf.json()['result']
            for board in demo_boards(reference):
                for row in board.rows:
                    for chart in row:
                        chart_id, context = records[str(scoped_identity('chart', chart.key))]
                        assert context['datasource']['id'] not in original_sources, chart.key
                        response = session.get(f'{base}/api/v1/chart/{chart_id}/data/', params={'force':'true'}, timeout=60)
                        response.raise_for_status()
                        result = response.json()['result'][0]
                        assert not result.get('error') and result.get('status') != 'failed', (chart.key, result)
                        assert DEMO_BI in result['query'], (chart.key, result['query'])
                        assert result['data'], ('Empty demo chart', chart.key)
                        if chart.kind == 'big_number_total':
                            metric = chart.metrics[0]
                            actual = float(result['data'][0][metric])
                            assert math.isclose(actual, expected[chart.dataset][metric], rel_tol=1e-10, abs_tol=1e-8), (chart.key, actual)
                        group_totals = {
                            'runs-outcomes':('runs',), 'runs-rulesets':('runs',), 'xp-completeness':('runs','missing_xp_runs','known_xp'),
                            'gifts-level':('claims',), 'gifts-bundles':('claims',),
                            'cases-rarity':('openings',), 'cases-catalog':('openings','guarantees'),
                            'shop-catalog':('purchases','prestige'), 'events-contracts':('events','late_events','clock_skew'),
                        }
                        for metric in group_totals.get(chart.key, ()):
                            total = sum(float(r[metric]) for r in result['data'])
                            assert math.isclose(total, expected[chart.dataset][metric], rel_tol=1e-10, abs_tol=1e-8), (chart.key,metric,total)
                        if chart.key == 'runs-rulesets':
                            for metric in ('avg_hero','avg_tower','avg_upgrades','win_rate'):
                                weighted = sum(float(r[metric])*r['runs'] for r in result['data']) / expected['runs']['runs']
                                assert math.isclose(weighted,expected['runs'][metric],rel_tol=1e-10,abs_tol=1e-8), metric
                        if chart.dataset == 'refresh_status':
                            assert len(result['data']) == 7
                            assert all(r['health']=='Актуально' for r in result['data'])
                            assert all(r['recovery_hint']=='Действий не требуется.' for r in result['data'])
                            # This is what the browser's raw-table builder sends, rather than
                            # relying only on the separately saved query_context.
                            rebuilt = deepcopy(context)
                            rebuilt['force'] = True
                            rebuilt['queries'][0]['columns'] = rebuilt['form_data']['all_columns']
                            response = session.post(base+'/api/v1/chart/data',json=rebuilt,timeout=60)
                            response.raise_for_status()
                            assert len(response.json()['result'][0]['data']) == 7
                        charts.append(dict(key=chart.key, id=chart_id, rows=len(result['data'])))
                        print(f'Filled demo chart {chart_id}: {len(result["data"])} rows — {chart.title}', flush=True)
                if board.filter_column:
                    first = board.rows[0][0]
                    _, context = records[str(scoped_identity('chart', first.key))]
                    context = deepcopy(context)
                    time_column = next(d.time_column for d in DATASETS if d.name == first.dataset)
                    start = reference.replace(hour=0,minute=0,second=0,microsecond=0)-timedelta(days=6)
                    end = start+timedelta(days=7)
                    period = f'{start:%Y-%m-%d} : {end:%Y-%m-%d}'
                    value = FILTER_SAMPLE[board.filter_column]
                    context['force'] = True
                    context['queries'][0].update(time_range=period,
                        filters=[{'col':board.filter_column, 'op':'IN', 'val':[value]}])
                    response = session.post(base+'/api/v1/chart/data', json=context, timeout=60)
                    response.raise_for_status()
                    result = response.json()['result'][0]
                    assert not result.get('error') and result['data'], (board.slug, result)
                    expression = next(d.metrics[first.metrics[0]][0] for d in DATASETS if d.name == first.dataset)
                    filter_expression = DISPLAY_COLUMNS.get(first.dataset, {}).get(board.filter_column,
                                            (identifier(board.filter_column),))[0]
                    sql = (f'SELECT {expression} FROM {DEMO_BI}.{first.dataset}_snapshot '
                           f'WHERE {identifier(time_column)} >= {{start:DateTime64(3)}} AND {identifier(time_column)} < {{end:DateTime64(3)}} '
                           f'AND {filter_expression} = {{value:String}}')
                    direct = float(admin.query(sql, parameters={'start':start, 'end':end, 'value':value}).first_row[0])
                    actual = float(result['data'][0][first.metrics[0]])
                    assert direct > 0 and math.isclose(actual, direct, rel_tol=1e-10,abs_tol=1e-8), (board.slug, actual, direct)
                    print(f'Demo period + category: {board.slug} / {value} = {actual}', flush=True)
        assert len(charts) == 45
        # A free offer is a successful issuance, not paid spending or a zero-priced paid average.
        shop_board = next(b for b in BOARDS if b.slug == 'vatican-shop')
        with requests.Session() as session:
            login = session.post(base+'/api/v1/security/login', json={
                'username':os.getenv('VD_SUPERSET_ADMIN_USERNAME','admin'),
                'password':os.environ['VD_SUPERSET_ADMIN_PASSWORD'],'provider':'db','refresh':True},timeout=30)
            login.raise_for_status()
            session.headers['Authorization']='Bearer '+login.json()['access_token']
            csrf=session.get(base+'/api/v1/security/csrf_token/',timeout=30);csrf.raise_for_status()
            session.headers['X-CSRFToken']=csrf.json()['result']
            for chart in shop_board.rows[0]:
                _, context=records[str(scoped_identity('chart',chart.key))]
                context=deepcopy(context);context['force']=True
                context['queries'][0]['filters']=[{'col':'offer_id','op':'IN','val':['weekly_gift']}]
                response=session.post(base+'/api/v1/chart/data',json=context,timeout=60);response.raise_for_status()
                result=response.json()['result'][0]
                assert not result.get('error'), (chart.key,result)
                value=result['data'][0][chart.metrics[0]]
                if chart.metrics[0]=='purchases':
                    assert value>0
                elif chart.metrics[0]=='paid_average':
                    assert value is None or math.isnan(float(value)), (chart.key,value)
                else:
                    assert value==0, (chart.key,value)
        print('Free offer: positive issuances, zero paid players/spending, unavailable paid average — OK',flush=True)
        print('PASS: 45 filled demo charts; all KPI match independent Python totals; five nonempty filter combinations; production datasets isolated', flush=True)
    finally:
        admin.close()


if __name__ == '__main__':
    main()
