"""Deduplicate once per refresh, serve exact timestamp filters from sorted snapshots.

No APPEND: replacing facts and late corrections must replace, not add to, BI data.
The source views remain available for immediate, uncached investigations in SQL Lab.
"""
import argparse
from pathlib import Path
import re

from catalog import BUSINESS_DATASETS


def identifier(value):
    if not re.fullmatch(r'[A-Za-z_][A-Za-z0-9_]{0,63}', value):
        raise ValueError('Invalid BI database or user identifier')
    return '`' + value + '`'


KEYS = dict(zip((s.name for s in BUSINESS_DATASETS),
                ('run_id', 'event_id', 'event_id', 'claim_id', 'opening_id', 'purchase_id', 'event_id')))


def health_select(jobs_sql):
    """Keep missing jobs visible, independently of the caller's join_use_nulls."""
    labels = ', '.join(repr(s.label) for s in BUSINESS_DATASETS)
    names = ', '.join(repr('refresh_' + s.name) for s in BUSINESS_DATASETS)
    return f"""SELECT *,
        transform(health_code,
            ['missing_job','refresh_failed','refresh_stopped','dependency_wait','never_succeeded','stale','unknown_state','ok'],
            ['Нет задачи обновления','Ошибка обновления','Обновление остановлено','Ожидание источников',
             'Нет успешного обновления','Данные устарели','Неизвестное состояние задачи','Актуально'], 'Неизвестное состояние') AS health,
        transform(health_code,
            ['missing_job','refresh_failed','refresh_stopped','dependency_wait','never_succeeded','stale','unknown_state','ok'],
            ['Администратору: повторить инициализацию BI.',
             'Проверьте журнал ClickHouse и повторите пересчёт.',
             'Администратору: запустить задачу обновления и дождаться успеха.',
             'Проверьте доступность зависимых витрин.',
             'Дождитесь первого успешного пересчёта.',
             'Проверьте расписание и последнюю попытку.',
             'Администратору: проверить состояние задачи.',
             'Действий не требуется.'], 'Администратору: проверить состояние задачи.') AS recovery_hint
        FROM (SELECT transform(expected, [{names}], [{labels}], expected) AS dataset,
            multiIf(ifNull(job_exists, 0) = 0, 'missing_job',
                ifNull(exception, '') != '', 'refresh_failed',
                ifNull(status, '') IN ('Disabled','Stopped'), 'refresh_stopped',
                ifNull(status, '') IN ('WaitingForDependencies','MissingDependencies'), 'dependency_wait',
                ifNull(status, '') NOT IN ('Scheduled','Running','RunningOnAnotherReplica','Scheduling'), 'unknown_state',
                isNull(last_success_time), 'never_succeeded',
                dateDiff('second', last_success_time, now()) > 600, 'stale', 'ok') AS health_code,
            last_success_time AS last_success,
            dateDiff('second', last_success_time, now()) AS age_seconds,
            next_refresh_time AS next_refresh, written_rows AS rows_written,
            last_success_duration_ms AS duration_ms, status AS refresh_state,
            last_refresh_time AS last_attempt
        FROM (SELECT arrayJoin([{names}]) AS expected) AS expected_jobs
        LEFT JOIN (SELECT *, toUInt8(1) AS job_exists FROM ({jobs_sql})) AS jobs
            ON jobs.view = expected_jobs.expected)"""


def warehouse_statements(source='vatican_analytics', target='vatican_bi'):
    if source == target:
        raise ValueError('BI must use a separate database')
    src, bi = identifier(source), identifier(target)
    yield 'CREATE DATABASE IF NOT EXISTS ' + bi
    for index, spec in enumerate(BUSINESS_DATASETS):
        detail = f'{bi}.{identifier(spec.name)}'
        snapshot = f'{bi}.{identifier(spec.serving_table)}'
        job = f'{bi}.{identifier("refresh_" + spec.name)}'
        timestamp = identifier(spec.time_column)
        yield f'CREATE OR REPLACE VIEW {detail} SQL SECURITY DEFINER AS ' + spec.sql.format(source=src).strip()
        yield (f'CREATE TABLE IF NOT EXISTS {snapshot} ENGINE = MergeTree '
               f'PARTITION BY toYYYYMM({timestamp}) ORDER BY ({timestamp}, {identifier(KEYS[spec.name])}) '
               f'SETTINGS index_granularity = 2048 AS SELECT * FROM {detail} LIMIT 0')
        yield (f'CREATE MATERIALIZED VIEW IF NOT EXISTS {job} '
               f'REFRESH EVERY 5 MINUTE OFFSET {index * 30} SECOND TO {snapshot} '
               f'SQL SECURITY DEFINER AS SELECT * FROM {detail} '
               'SETTINGS max_threads = 2, max_execution_time = 120, max_memory_usage = 536870912')
    yield f'CREATE OR REPLACE VIEW {bi}.refresh_status SQL SECURITY DEFINER AS ' + health_select(
        f"SELECT * FROM system.view_refreshes WHERE database = '{target}'")


def refresh_statements(target='vatican_bi'):
    bi = identifier(target)
    for spec in BUSINESS_DATASETS:
        job = f'{bi}.{identifier("refresh_" + spec.name)}'
        yield 'SYSTEM REFRESH VIEW ' + job
        yield 'SYSTEM WAIT VIEW ' + job


def provision(client, source, target):
    for statement in warehouse_statements(source, target):
        client.command(statement)
    for statement in refresh_statements(target):
        client.command(statement)
    # WAIT raises on failed refresh. Also guard against no completed initial snapshot.
    status = client.query(f'SELECT dataset, health FROM {identifier(target)}.refresh_status').result_rows
    if len(status) != len(BUSINESS_DATASETS) or any(health != 'Актуально' for _, health in status):
        raise RuntimeError('BI snapshots not ready: ' + repr(status))


def export_sql(directory, source='vatican_analytics', target='vatican_bi'):
    directory.mkdir(parents=True, exist_ok=True)
    header = '-- Generated by serving.py; no credentials. Run as warehouse administrator.\n'
    (directory / '01_serving.sql').write_text(header + ';\n\n'.join(warehouse_statements(source, target)) + ';\n')
    (directory / '02_refresh.sql').write_text(header + ';\n\n'.join(refresh_statements(target)) + ';\n')
    (directory / '03_health.sql').write_text(header + f'SELECT * FROM {identifier(target)}.refresh_status ORDER BY dataset;\n')
    checks = []
    for spec in BUSINESS_DATASETS:
        expressions = ', '.join(expression + ' AS ' + identifier(key)
                                for key, (expression, _) in spec.metrics.items())
        window = (f" WHERE {identifier(spec.time_column)} >= toStartOfDay(now('UTC')) - INTERVAL 29 DAY"
                  f" AND {identifier(spec.time_column)} < toStartOfDay(now('UTC')) + INTERVAL 1 DAY")
        for table in (spec.name, spec.serving_table):
            query = f'SELECT {expressions} FROM {identifier(target)}.{identifier(table)}' + window
            checks += [f'-- {table}: exact metrics for the same UTC period\n' + query,
                       'EXPLAIN indexes = 1 ' + query]
    (directory / '04_compare_and_explain.sql').write_text(
        '-- Read-only diagnostics. Compare source and snapshot after a successful refresh;\n'
        '-- concurrent ingestion can change the source between these queries.\n'
        'SET prefer_column_name_to_alias = 1;\n\n' + ';\n\n'.join(checks) + ';\n')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', default='vatican_analytics')
    parser.add_argument('--target', default='vatican_bi')
    parser.add_argument('--output', type=Path, default=Path(__file__).parent / 'sql')
    args = parser.parse_args()
    export_sql(args.output, args.source, args.target)
