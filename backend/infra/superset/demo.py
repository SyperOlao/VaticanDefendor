"""Persist isolated demonstration facts and independent copies of all dashboards."""
from datetime import datetime, timedelta, timezone
import fcntl
from hashlib import sha256
import json
import math
import os
from pathlib import Path

import psycopg2

from bootstrap import provision_superset
from catalog import BOARDS, BUSINESS_DATASETS
from demo_data import SEED_VERSION, TABLES, expected_metrics, generate, utc_reference
from demo_presentation import boards as readable_boards, DISPLAY_COLUMNS, METRIC_LABELS, COLUMN_LABELS, LABEL_COLORS
from presentation import identity
from serving import identifier, provision
from verify import client

DEMO_SOURCE = 'vatican_analytics_demo'
DEMO_BI = 'vatican_bi_demo'
SCOPE = 'demo'


def scoped_identity(kind, key):
    return identity(kind, SCOPE + ':' + key)


def demo_period(reference_at):
    day = reference_at.replace(hour=0, minute=0, second=0, microsecond=0)
    return f'{(day-timedelta(days=29)):%Y-%m-%d} : {(day+timedelta(days=1)):%Y-%m-%d}'


def demo_boards(reference_at):
    return readable_boards(reference_at)


def production_fingerprint(admin):
    source = identifier(os.getenv('VD_CLICKHOUSE_DB', 'vatican_analytics'))
    counts = {name: admin.query(f'SELECT count() FROM {source}.{name} FINAL').first_row[0] for name in TABLES}
    # Only this project's original metadata; do not inspect connection passwords.
    original_uuids = [str(identity('chart', chart.key)) for board in BOARDS for row in board.rows for chart in row]
    with psycopg2.connect(host=os.getenv('SUPERSET_METADATA_HOST', 'superset_metadata'), dbname='superset',
                         user='superset', password=os.environ['SUPERSET_METADATA_PASSWORD']) as conn:
        with conn.cursor() as cursor:
            cursor.execute('SELECT id, uuid, params, query_context FROM slices WHERE uuid::text = ANY(%s) ORDER BY id', (original_uuids,))
            metadata = cursor.fetchall()
            uuids = [str(identity('dashboard', board.slug)) for board in BOARDS]
            cursor.execute('SELECT id, uuid, position_json, json_metadata FROM dashboards WHERE uuid::text = ANY(%s) ORDER BY id', (uuids,))
            metadata += cursor.fetchall()
    return counts, sha256(repr(metadata).encode()).hexdigest()


def seed(admin):
    production = os.getenv('VD_CLICKHOUSE_DB', 'vatican_analytics')
    production_bi = os.getenv('VD_SUPERSET_BI_DATABASE', 'vatican_bi')
    if {DEMO_SOURCE, DEMO_BI} & {production, production_bi}:
        raise ValueError('Reserved demo databases collide with production configuration')
    db = identifier(DEMO_SOURCE)
    exists = admin.query('SELECT count() FROM system.databases WHERE name={db:String}', parameters={'db':DEMO_SOURCE}).first_row[0]
    if exists and not admin.query('EXISTS TABLE ' + db + '.demo_seed_manifest').first_row[0]:
        if admin.query('SELECT count() FROM system.tables WHERE database={db:String}', parameters={'db':DEMO_SOURCE}).first_row[0]:
            raise RuntimeError('Demo database name is occupied by unowned tables; refusing to modify it')
    admin.command('CREATE DATABASE IF NOT EXISTS ' + db)
    admin.command(f'''CREATE TABLE IF NOT EXISTS {db}.demo_seed_manifest (
        seed_key String, version UInt8, seed_version UInt16, reference_at DateTime64(3, 'UTC'),
        state String, counts_json String, expected_json String
    ) ENGINE=ReplacingMergeTree(version) ORDER BY seed_key''')
    manifest = admin.query(f"SELECT * FROM {db}.demo_seed_manifest FINAL WHERE seed_key='vatican-demo'").result_rows
    if manifest:
        _, _, version, reference_at, state, counts_json, expected_json = manifest[0]
        reference_at = utc_reference(reference_at)
        if version != SEED_VERSION:
            raise RuntimeError('Existing demo uses another seed version; explicit migration required')
        if state == 'ready':
            print('Reusing existing demo facts; fixed date window preserved', flush=True)
            return reference_at, json.loads(counts_json), json.loads(expected_json)
    else:
        reference_at = datetime.now(timezone.utc).replace(minute=0, second=0, microsecond=0)
        admin.insert(f'{DEMO_SOURCE}.demo_seed_manifest', [['vatican-demo', 1, SEED_VERSION, reference_at, 'loading', '{}', '{}']],
                     column_names=['seed_key','version','seed_version','reference_at','state','counts_json','expected_json'])
    for name in TABLES:
        admin.command(f'CREATE TABLE IF NOT EXISTS {db}.{name} AS {identifier(production)}.{name}')
    rows = generate(reference_at)
    counts = {}
    for table, values in rows.items():
        columns = list(values[0])
        # Stable IDs make recovery after a partial seed safe; FINAL removes retries.
        data = [[row[key] for key in columns] for row in values]
        duplicate_count = max(1, len(data)//50)
        admin.insert(f'{DEMO_SOURCE}.{table}', data + data[:duplicate_count], column_names=columns)
        counts[table] = dict(logical=len(data), deliberate_replays=duplicate_count)
        print(f'Demo {table}: {len(data)} facts + {duplicate_count} repeated deliveries', flush=True)
    expected = expected_metrics(rows, reference_at)
    admin.insert(f'{DEMO_SOURCE}.demo_seed_manifest', [['vatican-demo', 2, SEED_VERSION, reference_at, 'ready',
        json.dumps(counts), json.dumps(expected)]],
        column_names=['seed_key','version','seed_version','reference_at','state','counts_json','expected_json'])
    return reference_at, counts, expected


def verify_demo_metrics(admin, expected, reference_at):
    start, end = demo_period(reference_at).split(' : ')
    for dataset in BUSINESS_DATASETS:
        select = ', '.join(dataset.metrics[name][0] + ' AS ' + identifier(name) for name in expected[dataset.name])
        actual = next(admin.query(f'SELECT {select} FROM {identifier(DEMO_BI)}.{identifier(dataset.serving_table)} '
            f"WHERE {identifier(dataset.time_column)} >= '{start}' AND {identifier(dataset.time_column)} < '{end}'").named_results())
        for name, value in expected[dataset.name].items():
            if not math.isclose(float(actual[name]), value, rel_tol=1e-10, abs_tol=1e-8):
                raise AssertionError((dataset.name, name, actual[name], value))
        print('Independent Python totals agree: ' + dataset.name, flush=True)


def main():
    admin = client()
    try:
        before = production_fingerprint(admin)
        reference_at, counts, expected = seed(admin)
        for table, count in counts.items():
            actual = admin.query(f'SELECT count() FROM {identifier(DEMO_SOURCE)}.{table} FINAL').first_row[0]
            assert actual == count['logical'], (table, actual, count)
        provision(admin, DEMO_SOURCE, DEMO_BI)
        verify_demo_metrics(admin, expected, reference_at)
        reader = os.getenv('VD_SUPERSET_CLICKHOUSE_USER', 'vatican_superset')
        admin.command(f'GRANT SELECT ON {identifier(DEMO_BI)}.* TO {identifier(reader)}')
        app = provision_superset(DEMO_BI, reader, boards=demo_boards(reference_at), namespace=SCOPE,
                           period=demo_period(reference_at), title_prefix='ДЕМО · ', plain_legends=True,
                           calculated_columns=DISPLAY_COLUMNS, metric_labels=METRIC_LABELS,
                           column_labels=COLUMN_LABELS, chart_title_prefix='', label_colors=LABEL_COLORS)
        from players import provision_player_dashboard
        provision_player_dashboard(admin,DEMO_BI,reader,demo=True,reference_at=reference_at,superset_app=app)
        assert production_fingerprint(admin) == before, 'Production facts/metadata changed during demo provisioning'
        print('Production facts and original charts/dashboards unchanged', flush=True)
        print('Demo fixed period: ' + demo_period(reference_at), flush=True)
    finally:
        admin.close()


if __name__ == '__main__':
    with Path('/app/superset_home/vatican-init.lock').open('w') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        main()
