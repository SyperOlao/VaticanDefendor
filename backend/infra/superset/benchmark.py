"""Reproducible isolated benchmark of every active business chart (no production inserts)."""
import argparse
from datetime import datetime, timezone
from hashlib import sha256
import json
import math
from pathlib import Path
from statistics import median
from time import perf_counter
from uuid import uuid4

from catalog import BOARDS, BUSINESS_DATASETS
from serving import identifier, provision
from verify import client
import os


TABLES = ('fact_run_settlements', 'fact_xp_transactions', 'fact_player_level_ups',
          'fact_level_gift_claims', 'fact_case_settlements', 'fact_shop_purchases', 'raw_game_events')


def seed_statements(database, rows):
    db = identifier(database)
    uid = 'reinterpretAsUUID(MD5(toString(number)))'
    player = 'reinterpretAsUUID(MD5(toString(number % 1000)))'
    at = "toDateTime64('2025-11-18 00:00:00', 3, 'UTC') + toIntervalSecond(cityHash64(number) % 31536000)"
    common = dict(player_id=player)
    fixtures = [
        dict(run_id=uid, occurred_at=at, ruleset_id="concat('rules-', toString(number % 4))",
             content_release_id="'test'", outcome="if(number % 3 = 0, 'defeat', 'victory')",
             church_level='number % 8', hero_kills='number % 50', tower_kills='number % 30',
             building_upgrades='number % 12', xp_granted='if(number % 5 = 0, NULL, 80)',
             total_xp='1080', level_after='3'),
        dict(event_id=uid, occurred_at=at, run_id=uid, ruleset_id="'rules-1'", source_type="'DEDICATED_RUN'",
             xp_delta='80', level_after='3', hero_kill_xp='if(number % 5 = 0, NULL, 20)',
             tower_kill_xp='if(number % 5 = 0, NULL, 10)', tycoon_upgrade_xp='if(number % 5 = 0, NULL, 30)',
             victory_xp='if(number % 5 = 0, NULL, 20)'),
        dict(event_id=uid, occurred_at=at, run_id=uid, old_level='1', new_level='3', total_xp='1080'),
        dict(claim_id=uid, claimed_at=f'if(number % 7 = 0, NULL, {at})', rewarded_level='number % 20 + 1',
             reward_bundle_id=uid, claim_status="if(number % 4 = 0, 'PENDING', 'CLAIMED')"),
        dict(opening_id=uid, completed_at=at, case_id=uid, case_code="concat('case-', toString(number % 5))",
             ruleset_id="'test'", reward_bundle_id=uid, payment_mode="if(number % 2 = 0, 'INVENTORY', 'WALLET')",
             rarity="if(number % 5 = 0, 'RARE', 'COMMON')", pity_before='number % 10', pity_after='0',
             guaranteed_triggered='number % 10 = 0', entry_index='0'),
        dict(purchase_id=uid, operation_id=uid, completed_at=at, offer_id="concat('offer-', toString(number % 10))",
             catalog_version="'test'", currency_code="'PRESTIGE'", charged_amount='if(number % 4 = 0, 0, 25)',
             weapon_configuration_id="'sword.common'", weapon_instance_id=uid),
        dict(event_id=uid, occurred_at=at,
             received_at=f"({at}) + toIntervalSecond(multiIf(number % 10 = 0, -10, number % 3 = 0, 600, 10))",
             event_name="['run.completed','xp.granted','player.level_up','level_gift.claimed','case.opened','shop.purchase_confirmed'][number % 6 + 1]",
             source="if(number % 6 = 5, 'shop', 'game_backend')", schema_version='1', payload_json="'{}'"),
    ]
    for table, fixture in zip(TABLES, fixtures):
        values = {**common, **fixture}
        yield f'INSERT INTO {db}.{table} ({", ".join(values)}) SELECT {", ".join(values.values())} FROM numbers({rows})'
        yield f'INSERT INTO {db}.{table} SELECT * FROM {db}.{table} LIMIT {rows // 10}'


def query_sql(chart, dataset, database, optimized, start='2026-10-19 00:00:00', end='2026-11-18 00:00:00'):
    columns = list(chart.dimensions)
    if chart.kind == 'echarts_timeseries_line':
        columns.insert(0, f'toStartOfDay({identifier(dataset.time_column)})')
    metrics = [dataset.metrics[key][0] + ' AS ' + identifier(key) for key in chart.metrics]
    source = dataset.serving_table if optimized else dataset.name
    sql = (f'SELECT {", ".join(columns + metrics)} FROM {identifier(database)}.{identifier(source)} '
           f"WHERE {identifier(dataset.time_column)} >= '{start}' AND {identifier(dataset.time_column)} < '{end}'")
    if columns:
        sql += ' GROUP BY ' + ', '.join(columns)
        # Stable set/order for comparison; ordering does not affect date pruning.
        sql += ' ORDER BY ' + ', '.join(columns)
    return sql + ' LIMIT 1000'


def equal(left, right):
    if isinstance(left, (tuple, list)):
        return len(left) == len(right) and all(equal(a, b) for a, b in zip(left, right))
    if isinstance(left, float):
        return (math.isnan(left) and math.isnan(right)) or math.isclose(left, right, rel_tol=1e-10, abs_tol=1e-10)
    return left == right


def run(rows, repeats):
    admin = client()
    source = os.getenv('VD_CLICKHOUSE_DB', 'vatican_analytics')
    fixture = 'vatican_bi_bench_' + uuid4().hex
    bi = fixture + '_bi'
    try:
        admin.command('CREATE DATABASE ' + identifier(fixture))
        for table in TABLES:
            admin.command(f'CREATE TABLE {identifier(fixture)}.{table} AS {identifier(source)}.{table}')
        for sql in seed_statements(fixture, rows):
            admin.command(sql, settings={'max_threads': 2, 'max_memory_usage': 536870912})
        started = perf_counter()
        provision(admin, fixture, bi)
        refresh_seconds = perf_counter() - started
        # Freeze benchmark snapshots so scheduled refreshes cannot skew alternating measurements.
        for dataset in BUSINESS_DATASETS:
            admin.command(f'SYSTEM STOP VIEW {identifier(bi)}.refresh_{dataset.name}')
        datasets = {s.name: s for s in BUSINESS_DATASETS}
        results = []
        for board in BOARDS:
            for row in board.rows:
                for chart in row:
                    if chart.dataset not in datasets:
                        continue
                    dataset = datasets[chart.dataset]
                    measurements = {False: [], True: []}
                    values = {}
                    for repeat in range(repeats + 1):
                        for optimized in ((False, True) if repeat % 2 == 0 else (True, False)):
                            query = admin.query(query_sql(chart, dataset, bi, optimized),
                                settings={'max_threads': 2, 'use_query_cache': 0, 'max_memory_usage': 536870912})
                            values[optimized] = query.result_rows
                            if repeat:  # First pair warms filesystem/metadata; report warm uncached queries.
                                measurements[optimized].append(query.summary)
                        assert equal(values[False], values[True]), ('Metric mismatch', chart.key)
                    # Also verify a non-midnight interval to catch accidental daily-grain approximations.
                    interval = dict(start='2026-11-16 12:34:56', end='2026-11-17 06:12:34')
                    before = admin.query(query_sql(chart, dataset, bi, False, **interval)).result_rows
                    after = admin.query(query_sql(chart, dataset, bi, True, **interval)).result_rows
                    assert equal(before, after), ('Sub-day mismatch', chart.key)
                    def summarize(optimized):
                        runs = measurements[optimized]
                        return {key: median(float(r[key]) for r in runs) for key in ('read_rows', 'read_bytes', 'elapsed_ns')}
                    results.append(dict(chart=chart.key, dataset=dataset.name, before=summarize(False), after=summarize(True)))
        totals = {mode: {key: sum(row[mode][key] for row in results)
                         for key in ('read_rows', 'read_bytes', 'elapsed_ns')} for mode in ('before', 'after')}
        return dict(measured_at_utc=datetime.now(timezone.utc).isoformat(), clickhouse_version=admin.query('SELECT version()').first_row[0],
                    catalog_sha256=sha256(Path(__file__).with_name('catalog.py').read_bytes()).hexdigest(),
                    fixture_rows_per_source=rows, duplicate_rows_per_source=rows // 10,
                    source_count=7, history_days=365, repeats=repeats, cache='ClickHouse query cache disabled; filesystem warm',
                    chart_count=len(results), refresh_and_ddl_seconds=round(refresh_seconds, 3), totals=totals,
                    read_rows_reduction=totals['before']['read_rows'] / totals['after']['read_rows'],
                    query_time_reduction=totals['before']['elapsed_ns'] / totals['after']['elapsed_ns'], charts=results)
    finally:
        try:
            admin.command('DROP DATABASE IF EXISTS ' + identifier(bi) + ' SYNC')
            admin.command('DROP DATABASE IF EXISTS ' + identifier(fixture) + ' SYNC')
        finally:
            admin.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--rows', type=int, default=300000)
    parser.add_argument('--repeats', type=int, default=3)
    parser.add_argument('--report', type=Path)
    args = parser.parse_args()
    if args.rows < 1000 or args.repeats < 1:
        parser.error('Use at least 1000 rows and 1 measured repeat')
    result = run(args.rows, args.repeats)
    output = json.dumps(result, ensure_ascii=False, indent=2)
    if args.report:
        args.report.write_text(output + '\n')
    print(output)
