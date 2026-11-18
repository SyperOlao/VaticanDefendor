"""Verify native chart queries, read-only access and SQL metrics on disposable facts."""
from datetime import datetime, timedelta, timezone
import json
import os
from uuid import uuid4

import clickhouse_connect
from clickhouse_connect.driver.exceptions import ClickHouseError
import requests
import psycopg2

from bootstrap import identifier
from catalog import BOARDS, BUSINESS_DATASETS
from presentation import identity
from serving import provision, refresh_statements


def client(reader=False):
    return clickhouse_connect.get_client(host=os.getenv('VD_CLICKHOUSE_HOST', 'clickhouse'), port=8123,
        username=os.environ['VD_SUPERSET_CLICKHOUSE_USER' if reader else 'VD_CLICKHOUSE_USER'],
        password=os.environ['VD_SUPERSET_CLICKHOUSE_PASSWORD' if reader else 'VD_CLICKHOUSE_PASSWORD'],
        connect_timeout=5, send_receive_timeout=180, settings={} if reader else {'prefer_column_name_to_alias': 1})


def verify_sql():
    admin = client()
    reader = client(reader=True)
    source = identifier(os.getenv('VD_CLICKHOUSE_DB', 'vatican_analytics'))
    bi = identifier(os.getenv('VD_SUPERSET_BI_DATABASE', 'vatican_bi'))
    scratch = identifier('vatican_bi_check_' + uuid4().hex)
    serving = identifier(scratch.strip('`') + '_serving')
    try:
        assert str(reader.query("SELECT value FROM system.settings WHERE name='readonly'").first_row[0]) == '1'
        assert reader.query(f'SELECT count() FROM {bi}.runs').row_count == 1
        try:
            reader.query(f'SELECT * FROM {source}.raw_game_events LIMIT 0')
        except ClickHouseError as error:
            if 'ACCESS_DENIED' not in str(error) and 'Not enough privileges' not in str(error):
                raise
        else:
            raise AssertionError('BI reader unexpectedly has access to raw warehouse tables')
        print('BI reader: SELECT on views; readonly=1; raw table access denied')
        admin.command('CREATE DATABASE ' + scratch)
        tables = ('fact_run_settlements', 'fact_xp_transactions', 'fact_player_level_ups',
                  'fact_level_gift_claims', 'fact_case_settlements', 'fact_shop_purchases', 'raw_game_events')
        for name in tables:
            admin.command(f'CREATE TABLE {scratch}.{name} AS {source}.{name}')
        for spec in BUSINESS_DATASETS:
            admin.command(f'CREATE VIEW {scratch}.bi_{spec.name} AS ' + spec.sql.format(source=scratch))
        player, run = uuid4(), uuid4()
        at = datetime(2026, 11, 17, 0, 0, 0, tzinfo=timezone.utc)

        def insert(table, row, repeat=True):
            values = list(row.values())
            admin.insert(f'{scratch}.{table}', [values, values] if repeat else [values], column_names=list(row))

        run_row = dict(run_id=run, player_id=player, occurred_at=at, ruleset_id='bi-check', content_release_id='bi-check',
                       outcome='victory', church_level=3, hero_kills=4, tower_kills=5, building_upgrades=3,
                       xp_granted=80, total_xp=1080, level_after=3)
        insert('fact_run_settlements', run_row)
        insert('fact_run_settlements', {**run_row, 'run_id': uuid4(), 'outcome': 'defeat', 'xp_granted': None,
                                       'total_xp': None, 'level_after': None})
        insert('fact_run_settlements', {**run_row, 'run_id': uuid4(), 'xp_granted': 0,
                                       'occurred_at': at + timedelta(milliseconds=501)})
        insert('fact_xp_transactions', dict(event_id=uuid4(), player_id=player, occurred_at=at,
            run_id=run, source_type='DEDICATED_RUN', xp_delta=80, level_after=3, hero_kill_xp=20,
            tower_kill_xp=10, tycoon_upgrade_xp=30, victory_xp=20))
        insert('fact_player_level_ups', dict(event_id=uuid4(), player_id=player, occurred_at=at,
                                           run_id=run, old_level=1, new_level=3, total_xp=1080))
        gift = dict(claim_id=uuid4(), player_id=player, claimed_at=at, rewarded_level=3,
                    reward_bundle_id=uuid4(), claim_status='CLAIMED')
        insert('fact_level_gift_claims', gift)
        insert('fact_level_gift_claims', {**gift, 'claim_id': uuid4(), 'claim_status': 'PENDING'})
        insert('fact_level_gift_claims', {**gift, 'claim_id': uuid4(), 'claimed_at': None})
        insert('fact_case_settlements', dict(opening_id=uuid4(), player_id=player, completed_at=at,
            case_id=uuid4(), case_code='bi-check', ruleset_id='bi-check', reward_bundle_id=uuid4(),
            payment_mode='INVENTORY', rarity='RARE', pity_before=3, pity_after=0,
            guaranteed_triggered=1, entry_index=0))
        purchase = dict(purchase_id=uuid4(), operation_id=uuid4(), player_id=player, completed_at=at,
            offer_id='bi-check', catalog_version='bi-check', currency_code='PRESTIGE', charged_amount=25,
            weapon_configuration_id='sword.common', weapon_instance_id=uuid4())
        insert('fact_shop_purchases', purchase)
        insert('fact_shop_purchases', {**purchase, 'purchase_id': uuid4(), 'operation_id': uuid4(),
                                     'weapon_instance_id': uuid4(), 'charged_amount': 0})
        for name in ('run.completed', 'xp.granted', 'shop.purchase_confirmed'):
            insert('raw_game_events', dict(event_id=uuid4(), player_id=player, occurred_at=at,
                received_at=at + timedelta(seconds=10), event_name=name,
                source='shop' if name.startswith('shop.') else 'game_backend', schema_version=1, payload_json='{}'))
        expected = {
            'runs': {'runs': 3, 'players': 1, 'victories': 2, 'win_rate': 2/3, 'known_xp': 80,
                     'missing_xp_runs': 1, 'xp_coverage': 2/3},
            'xp': {'xp': 80, 'hero_xp': 20, 'tower_xp': 10, 'tycoon_xp': 30, 'victory_xp': 20, 'transactions': 1},
            'level_ups': {'transitions': 1, 'levels': 2, 'players': 1},
            'level_gifts': {'claims': 1, 'players': 1},
            'case_openings': {'openings': 1, 'players': 1, 'guarantee_rate': 1, 'avg_pity': 3},
            'shop_purchases': {'purchases': 2, 'players': 1, 'prestige': 25, 'average_price': 12.5, 'free_rate': .5},
            'event_delivery': {'events': 3, 'event_types': 3, 'average_lag': 10, 'late_events': 0, 'clock_skew': 0},
        }
        for spec in BUSINESS_DATASETS:
            query = ', '.join(f'{expression} AS {identifier(key)}' for key, (expression, _) in spec.metrics.items())
            actual = next(admin.query(f'SELECT {query} FROM {scratch}.bi_{spec.name}').named_results())
            for key, value in expected[spec.name].items():
                assert actual[key] == value, (spec.name, key, actual[key], value)
            assert admin.query(f"SELECT count() FROM {scratch}.bi_{spec.name} WHERE occurred_at >= '2026-11-18 00:00:00'").first_row[0] == 0
        print('All 7 view metric sets passed: FINAL, unknown XP, claims, free purchases and UTC boundaries')
        for lag in (-5, 600):
            insert('raw_game_events', dict(event_id=uuid4(), player_id=player, occurred_at=at,
                received_at=at + timedelta(seconds=lag), event_name='case.opened',
                source='game_backend', schema_version=1, payload_json='{}'))
        assert admin.query(f'SELECT avgIf(lag_seconds, lag_seconds >= 0), countIf(lag_seconds < 0), countIf(lag_seconds > 300) '
                           f'FROM {scratch}.bi_event_delivery').first_row == (157.5, 1, 1)
        # Operational period must follow receipt time, including a cross-midnight clock error.
        assert admin.query(f"SELECT count() FROM {scratch}.bi_event_delivery WHERE received_at < '2026-11-17'").first_row[0] == 1
        insert('fact_shop_purchases', {**purchase, 'purchase_id': uuid4(), 'player_id': uuid4(),
            'operation_id': uuid4(), 'weapon_instance_id': uuid4(), 'currency_code': 'OTHER', 'charged_amount': 999})
        shop_metrics = next(s.metrics for s in BUSINESS_DATASETS if s.name == 'shop_purchases')
        shop_sql = ', '.join(shop_metrics[key][0] for key in ('prestige', 'average_price', 'paid_average', 'paid_players'))
        assert admin.query(f'SELECT {shop_sql} FROM {scratch}.bi_shop_purchases').first_row == (25, 12.5, 25, 1)
        provision(admin, scratch.strip('`'), serving.strip('`'))
        for spec in BUSINESS_DATASETS:
            expressions = ', '.join(value[0] for value in spec.metrics.values())
            # Exact period filters, including a sub-day interval, retain source semantics.
            for period in ('', f" WHERE {spec.time_column} >= '2026-11-17 00:00:00' AND {spec.time_column} < '2026-11-17 00:00:30'"):
                expected_rows = admin.query(f'SELECT {expressions} FROM {scratch}.bi_{spec.name}' + period).result_rows
                actual_rows = admin.query(f'SELECT {expressions} FROM {serving}.{spec.serving_table}' + period).result_rows
                assert repr(actual_rows) == repr(expected_rows), (spec.name, period, actual_rows, expected_rows)
        # Repeated refresh is replacement, not a second append of the same events.
        for statement in refresh_statements(serving.strip('`')):
            admin.command(statement)
        assert admin.query(f'SELECT count() FROM {serving}.runs_snapshot').first_row[0] == 3
        assert admin.query(f"SELECT count() FROM {serving}.runs_snapshot WHERE occurred_at >= '2026-11-17 00:00:00.501' "
                           "AND occurred_at < '2026-11-17 00:00:00.502'").first_row[0] == 1
        insert('fact_run_settlements', {**run_row, 'xp_granted': 120,
            'occurred_at': at - timedelta(days=1), 'calculated_at': datetime.now(timezone.utc) + timedelta(minutes=1)})
        admin.command(f'SYSTEM REFRESH VIEW {serving}.refresh_runs')
        admin.command(f'SYSTEM WAIT VIEW {serving}.refresh_runs')
        assert admin.query(f'SELECT count(), sum(known_xp) FROM {serving}.runs_snapshot').first_row == (3, 120)
        assert admin.query(f"SELECT count() FROM {serving}.runs_snapshot WHERE occurred_at >= '2026-11-17'").first_row[0] == 2
        # A failed rebuild must retain the previous successful snapshot and show an error.
        admin.command(f'ALTER TABLE {serving}.refresh_runs MODIFY REFRESH EVERY 5 MINUTE SETTINGS refresh_retries=0')
        admin.command(f'RENAME TABLE {scratch}.fact_run_settlements TO {scratch}.runs_temporarily_unavailable')
        try:
            admin.command(f'SYSTEM REFRESH VIEW {serving}.refresh_runs')
            try:
                admin.command(f'SYSTEM WAIT VIEW {serving}.refresh_runs')
            except ClickHouseError:
                pass
            else:
                raise AssertionError('Failed refresh unexpectedly succeeded')
            assert admin.query(f'SELECT count(), sum(known_xp) FROM {serving}.runs_snapshot').first_row == (3, 120)
            assert admin.query(f"SELECT health FROM {serving}.refresh_status WHERE dataset = 'Подтверждённые забеги'").first_row[0] == 'Ошибка обновления'
        finally:
            admin.command(f'RENAME TABLE {scratch}.runs_temporarily_unavailable TO {scratch}.fact_run_settlements')
        print('All 7 snapshots: equivalent metrics, exact time filters, retry, late correction and failed-refresh retention passed')
    finally:
        try:
            admin.command('DROP DATABASE IF EXISTS ' + serving + ' SYNC')
            admin.command('DROP DATABASE IF EXISTS ' + scratch + ' SYNC')
        finally:
            reader.close()
            admin.close()


def verify_charts():
    with psycopg2.connect(host=os.getenv('SUPERSET_METADATA_HOST', 'superset_metadata'),
            dbname='superset', user='superset', password=os.environ['SUPERSET_METADATA_PASSWORD']) as metadata:
        with metadata.cursor() as cursor:
            cursor.execute('SELECT id, uuid, query_context FROM slices')
            records = cursor.fetchall()
            ids = {str(uuid): chart_id for chart_id, uuid, _ in records}
            contexts = {chart_id: json.loads(context) for chart_id, _, context in records if context}
    charts = [(ids[str(identity('chart', spec.key))], spec)
              for board in BOARDS for row in board.rows for spec in row]
    base = os.getenv('VD_SUPERSET_VERIFY_URL', 'http://superset:8088').rstrip('/')
    with requests.Session() as session:
        login = session.post(base + '/api/v1/security/login', json={
            'username': os.getenv('VD_SUPERSET_ADMIN_USERNAME', 'admin'),
            'password': os.environ['VD_SUPERSET_ADMIN_PASSWORD'], 'provider': 'db', 'refresh': True}, timeout=30)
        login.raise_for_status()
        session.headers['Authorization'] = 'Bearer ' + login.json()['access_token']
        for chart_id, spec in charts:
            response = session.get(f'{base}/api/v1/chart/{chart_id}/data/', params={'force': 'true'}, timeout=60)
            if response.status_code != 200:
                raise RuntimeError(f'Chart {chart_id} ({spec.title}): HTTP {response.status_code}: {response.text[:800]}')
            for result in response.json()['result']:
                assert not result.get('error') and result.get('status') != 'failed', (chart_id, result.get('error'))
                if spec.dataset != 'refresh_status':
                    assert spec.dataset + '_snapshot' in result['query'], (chart_id, result['query'])
                    assert ' FINAL' not in result['query'], (chart_id, result['query'])
            print(f'Chart {chart_id}: OK — {spec.title}')
        csrf = session.get(base + '/api/v1/security/csrf_token/', timeout=30)
        csrf.raise_for_status()
        session.headers['X-CSRFToken'] = csrf.json()['result']
        for board in BOARDS:
            if not board.filter_column:
                continue
            spec = board.rows[0][0]
            chart_id = ids[str(identity('chart', spec.key))]
            context = contexts[chart_id]
            context['force'] = True
            context['queries'][0]['filters'].append({'col': board.filter_column, 'op': 'IN', 'val': ['__bi_no_match__']})
            response = session.post(base + '/api/v1/chart/data', json=context, timeout=60)
            response.raise_for_status()
            result = response.json()['result'][0]
            assert not result.get('error') and board.filter_column in result['query'], (board.slug, result)
            # Selector queries also use the period and correct main timestamp.
            context['queries'][0].update(columns=[board.filter_column], metrics=[], orderby=[[board.filter_column, True]],
                                          filters=[], post_processing=[])
            response = session.post(base + '/api/v1/chart/data', json=context, timeout=60)
            response.raise_for_status()
            result = response.json()['result'][0]
            timestamp = next(s.time_column for s in BUSINESS_DATASETS if s.name == spec.dataset)
            assert not result.get('error') and timestamp in result['query'] and '_snapshot' in result['query'], (board.slug, result)
            print('Period + category and selector: OK — ' + board.slug)
    print(f'All {len(charts)} native chart query contexts passed')


if __name__ == '__main__':
    import argparse
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--sql-only', action='store_true', help='Check views without the Superset HTTP server')
    args = parser.parse_args()
    verify_sql()
    if not args.sql_only:
        verify_charts()
