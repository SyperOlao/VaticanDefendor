"""Cross-check player isolation, period changes, empty selections and native contexts."""
from copy import deepcopy
from datetime import timedelta
import json
import math
import os
from time import perf_counter
from uuid import UUID, uuid4

import psycopg2
import requests

from demo_data import utc_reference
from player_catalog import PLAYER_DATASET, board
from presentation import identity
from serving import identifier
from verify import client

TABLES=('runs','xp','level_ups','level_gifts','case_openings','shop_purchases')


def totals(admin,target,player,start,end):
    # Read each independent source; never infer one record type from another.
    params={'player':UUID(player),'start':start,'end':end}
    where=' WHERE player_id={player:UUID} AND occurred_at>={start:DateTime64(3)} AND occurred_at<{end:DateTime64(3)}'
    def query(table,expressions):
        return admin.query(f'SELECT {expressions} FROM {identifier(target)}.{table}_snapshot'+where,parameters=params).first_row
    runs,wins,hero,tower,upgrades=query('runs',"count(),countIf(outcome='victory'),sum(toDecimal128(hero_kills,0)),sum(toDecimal128(tower_kills,0)),sum(toDecimal128(building_upgrades,0))")
    purchases,prestige=query('shop_purchases',"count(),sumIf(charged_amount,currency_code='PRESTIGE')")
    days=set()
    record_count=0
    for table in TABLES:
        rows=admin.query(f'SELECT toDate(occurred_at), count() FROM {identifier(target)}.{table}_snapshot'+where+' GROUP BY toDate(occurred_at)',parameters=params).result_rows
        days.update(d for d,_ in rows)
        record_count+=sum(n for _,n in rows)
    return dict(runs=runs,win_rate=wins/runs if runs else None,xp=query('xp','sum(xp_delta)')[0],
        levels=query('level_ups','sum(levels_gained)')[0],purchases=purchases,prestige=prestige,
        gifts=query('level_gifts','count()')[0],cases=query('case_openings','count()')[0],
        hero_kills=hero,tower_kills=tower,upgrades=upgrades,active_days=len(days),records=record_count)


def main():
    admin=client()
    try:
        reference=utc_reference(admin.query("SELECT reference_at FROM vatican_analytics_demo.demo_seed_manifest FINAL WHERE seed_key='vatican-demo' AND state='ready'").first_row[0])
        day=reference.replace(hour=0,minute=0,second=0,microsecond=0)
        start,end=day-timedelta(days=29),day+timedelta(days=1)
        players=[r[0] for r in admin.query('SELECT player_id FROM vatican_bi_demo.player_activity '
            'WHERE occurred_at>={start:DateTime64(3)} AND occurred_at<{end:DateTime64(3)} '
            'GROUP BY player_id HAVING uniqExact(activity_type)=6 ORDER BY count() DESC,player_id LIMIT 2',
            parameters={'start':start,'end':end}).result_rows]
        assert len(players)==2
        for target in (os.getenv('VD_SUPERSET_BI_DATABASE','vatican_bi'),'vatican_bi_demo'):
            count,unique=admin.query(f'SELECT count(),uniqExact(record_id) FROM {identifier(target)}.player_activity').first_row
            independent=sum(admin.query(f'SELECT count() FROM {identifier(target)}.{table}_snapshot').first_row[0] for table in TABLES)
            assert count==unique==independent,(target,count,unique,independent)
        base=os.getenv('SUPERSET_VERIFY_URL','http://superset:8088')
        with psycopg2.connect(host=os.environ['SUPERSET_METADATA_HOST'],dbname='superset',user='superset',password=os.environ['SUPERSET_METADATA_PASSWORD']) as conn:
            with conn.cursor() as cursor:
                cursor.execute('SELECT id,uuid::text,query_context FROM slices')
                saved={u:(i,json.loads(q)) for i,u,q in cursor.fetchall() if q}
                cursor.execute('SELECT json_metadata FROM dashboards WHERE uuid=%s',(str(identity('dashboard','demo:vatican-demo-player')),))
                config=json.loads(cursor.fetchone()[0])['native_filter_configuration']
        player_filter=next(f for f in config if f['name']=='Игрок · ID')
        assert player_filter['controlValues']['enableEmptyFilter'] and not player_filter['controlValues']['multiSelect']
        dataset_id=player_filter['targets'][0]['datasetId']
        specs=[c for row in board(True,reference).rows for c in row]
        assert len(specs)==14
        with requests.Session() as session:
            login=session.post(base+'/api/v1/security/login',json={'username':os.getenv('VD_SUPERSET_ADMIN_USERNAME','admin'),
                'password':os.environ['VD_SUPERSET_ADMIN_PASSWORD'],'provider':'db','refresh':True},timeout=30)
            login.raise_for_status()
            session.headers['Authorization']='Bearer '+login.json()['access_token']
            csrf=session.get(base+'/api/v1/security/csrf_token/',timeout=30);csrf.raise_for_status()
            session.headers['X-CSRFToken']=csrf.json()['result']
            for scope in ('','demo:'):
                for spec in specs:
                    chart_id,context=saved[str(identity('chart',scope+spec.key))]
                    q=context['queries'][0]
                    assert any(f['col']=='player_id' and len(f['val'])==1 for f in q['filters']) or q['extras']['where']=='1 = 0'
                    response=session.get(f'{base}/api/v1/chart/{chart_id}/data/',params={'force':'true'},timeout=60)
                    response.raise_for_status()
                    assert not response.json()['result'][0].get('error')
            print('PASS: all 28 saved production/demo chart contexts; each defaults to one player or no rows')

            def run(spec,selected,first,last):
                _,context=saved[str(identity('chart','demo:'+spec.key))]
                context=deepcopy(context);context['force']=True
                q=context['queries'][0]
                q['time_range']=f'{first:%Y-%m-%d} : {last:%Y-%m-%d}'
                q['filters']=[f for f in q['filters'] if f['col']!='player_id']
                q['extras']['where']='' if selected else '1 = 0'
                if selected:
                    q['filters'].append({'col':'player_id','op':'IN','val':selected})
                # Raw tables must also work when reconstructed from native form data.
                if spec.kind=='table' and not spec.metrics:
                    q['columns']=context['form_data']['all_columns']
                response=session.post(base+'/api/v1/chart/data',json=context,timeout=60)
                assert response.ok,(spec.key,response.status_code,response.text[:1500])
                result=response.json()['result'][0]
                assert not result.get('error'),result
                return result

            for player in players:
                for first in (start,end-timedelta(days=7),end-timedelta(days=1)):
                    expected=totals(admin,'vatican_bi_demo',player,first,end)
                    for spec in specs:
                        result=run(spec,[player],first,end)
                        data=result['data']
                        if spec.kind=='big_number_total':
                            metric=spec.metrics[0];actual=data[0][metric];value=expected[metric]
                            if value is None:
                                assert actual is None or math.isnan(float(actual))
                            else:
                                assert math.isclose(float(actual),float(value),rel_tol=1e-10,abs_tol=1e-8),(spec.key,actual,value)
                        elif spec.key=='player-summary':
                            if not expected['records']:
                                assert not data
                            else:
                                assert len(data)==1 and data[0]['player_id']==player
                                for metric in spec.metrics:
                                    assert float(data[0][metric])==float(expected[metric]),(metric,data[0],expected)
                        elif spec.key=='player-daily':
                            total=sum(float(value) for row in data for key,value in row.items() if key!='occurred_at' and value is not None)
                            assert total==expected['records'],(total,expected['records'])
                        elif spec.key=='player-spend':
                            assert math.isclose(sum(float(r['prestige']) for r in data),float(expected['prestige']))
                        else:
                            key={'player-purchases-history':'purchases','player-runs-history':'runs','player-timeline':'records'}[spec.key]
                            assert len(data)==min(expected[key],1000),(spec.key,len(data),expected[key])
                            assert [r['occurred_at'] for r in data]==sorted((r['occurred_at'] for r in data),reverse=True)
                        assert player in result['query'] and 'player_id' in result['query']
                    print(f'PASS: 14 charts, player={player}, window={(end-first).days} days, independent totals and chronological histories')
            for selected in ([],[str(uuid4())]):
                for spec in specs:
                    data=run(spec,selected,start,end)['data']
                    if spec.kind=='big_number_total':
                        value=data[0][spec.metrics[0]]
                        assert value is None or math.isnan(float(value)) or float(value)==0,(spec.key,data)
                    else:
                        assert not data,(spec.key,data)
            print('PASS: clearing player and unknown ID never return aggregate statistics or another player history')
            query={'datasource':{'id':dataset_id,'type':'table'},'force':True,'queries':[{
                'columns':['player_id'],'metrics':[],'filters':[],'orderby':[['player_id',True]],'row_limit':1000,
                'time_range':f'{start:%Y-%m-%d} : {end:%Y-%m-%d}','granularity':'occurred_at'}],
                'result_format':'json','result_type':'full'}
            response=session.post(base+'/api/v1/chart/data',json=query,timeout=60);response.raise_for_status()
            assert set(players)<={r['player_id'] for r in response.json()['result'][0]['data']}
            print('PASS: player selector returns identities across all six record types')
        samples=[]
        for _ in range(3):
            t=perf_counter()
            admin.query('SELECT '+', '.join(v[0] for v in PLAYER_DATASET.metrics.values())+
                ' FROM vatican_bi_demo.player_activity WHERE player_id={player:String} '
                'AND occurred_at>={start:DateTime64(3)} AND occurred_at<{end:DateTime64(3)}',
                parameters={'player':players[0],'start':start,'end':end})
            samples.append(round((perf_counter()-t)*1000,2))
        print('Filtered union view, all metrics, 3 requests (ms): '+json.dumps(samples))
        print('PASS: source isolation, stable record IDs, no join multiplication; 2 players × 3 periods; all 14 player charts')
    finally:
        admin.close()


if __name__=='__main__':
    main()
