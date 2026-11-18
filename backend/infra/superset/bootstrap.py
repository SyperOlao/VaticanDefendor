"""Idempotently provision this project's ClickHouse views and native Superset objects."""
import json
import os
import re
from urllib.parse import quote

import clickhouse_connect

from catalog import BOARDS, DATASETS, COLUMN_LABELS
from presentation import chart_payload, filters, identity, layout
from serving import identifier, provision


def provision_warehouse():
    source = os.getenv('VD_CLICKHOUSE_DB', 'vatican_analytics')
    target = os.getenv('VD_SUPERSET_BI_DATABASE', 'vatican_bi')
    user = os.getenv('VD_SUPERSET_CLICKHOUSE_USER', 'vatican_superset')
    source_sql, target_sql, user_sql = map(identifier, (source, target, user))
    if source == target:
        raise ValueError('BI views must use a separate database')
    client = clickhouse_connect.get_client(host=os.getenv('VD_CLICKHOUSE_HOST', 'clickhouse'), port=8123,
        username=os.environ['VD_CLICKHOUSE_USER'], password=os.environ['VD_CLICKHOUSE_PASSWORD'],
        connect_timeout=5, send_receive_timeout=180)
    try:
        provision(client, source, target)
        password = {'password': os.environ['VD_SUPERSET_CLICKHOUSE_PASSWORD']}
        client.command(f'CREATE USER IF NOT EXISTS {user_sql} IDENTIFIED WITH sha256_password BY %(password)s', parameters=password)
        client.command(f'ALTER USER {user_sql} IDENTIFIED WITH sha256_password BY %(password)s '
                       'SETTINGS readonly=1, max_execution_time=30, max_threads=2, max_memory_usage=536870912, '
                       'prefer_column_name_to_alias=1', parameters=password)
        client.command(f'GRANT SELECT ON {target_sql}.* TO {user_sql}')
    finally:
        client.close()
    return target, user


def metric_format(name):
    if name.endswith(('_rate', '_coverage')):
        return '.1%'
    return ',.1f' if name.startswith(('avg_', 'average_')) or name in ('paid_average', 'per_player') else ',.0f'


def attribute_owner(model, owner):
    if model.created_by_fk is None:
        model.created_by_fk = owner.id
    model.changed_by_fk = owner.id


def provision_superset(target, reader, *, boards=None, namespace='', period=None, title_prefix='', plain_legends=False,
                       calculated_columns=None, metric_labels=None, column_labels=None, chart_title_prefix=None,
                       label_colors=None, dashboard_css=None, dataset_specs=None, navigation_boards=None,
                       filter_defaults=None, superset_app=None):
    dataset_specs = DATASETS if dataset_specs is None else dataset_specs
    if boards is None:
        from production_presentation import boards as production_boards, METRIC_LABELS, DESCRIPTION_CSS
        boards = production_boards()
        if metric_labels is None:
            metric_labels = METRIC_LABELS
        if dashboard_css is None:
            dashboard_css = DESCRIPTION_CSS
    def object_identity(kind, key):
        return identity(kind, namespace + ':' + key if namespace else key)

    from superset.app import create_app
    app = superset_app or create_app()
    with app.app_context():
        from superset import db, security_manager
        from superset.models.core import Database
        from superset.models.dashboard import Dashboard
        from superset.models.slice import Slice
        from superset.connectors.sqla.models import SqlaTable, SqlMetric, TableColumn
        username = os.getenv('VD_SUPERSET_ADMIN_USERNAME', 'admin')
        admin = security_manager.find_user(username=username)
        if admin is None:
            admin = security_manager.add_user(username=username, first_name='VaticanDefendor', last_name='Admin',
                email=os.getenv('VD_SUPERSET_ADMIN_EMAIL', 'admin@vatican.local'),
                role=security_manager.find_role('Admin'), password=os.environ['VD_SUPERSET_ADMIN_PASSWORD'])
            if not admin:
                raise RuntimeError('Could not create the local Superset administrator')
        database = db.session.query(Database).filter_by(uuid=object_identity('database', 'vatican-bi')).one_or_none()
        if database is None:
            database = Database(database_name=title_prefix + 'VaticanDefendor · ClickHouse', uuid=object_identity('database', 'vatican-bi'))
            db.session.add(database)
        database.sqlalchemy_uri = ('clickhousedb://' + quote(reader, safe='') + ':'
            + quote(os.environ['VD_SUPERSET_CLICKHOUSE_PASSWORD'], safe='') + '@'
            + os.getenv('VD_CLICKHOUSE_HOST', 'clickhouse') + ':8123/' + quote(target, safe=''))
        database.expose_in_sqllab = True
        database.allow_run_async = False
        database.allow_dml = False
        database.allow_file_upload = False
        attribute_owner(database, admin)
        database.extra = json.dumps({'engine_params': {'pool_pre_ping': True,
            'connect_args': {'connect_timeout': 5, 'send_receive_timeout': 30}}})
        db.session.flush()

        datasets = {}
        for spec in dataset_specs:
            dataset = db.session.query(SqlaTable).filter_by(uuid=object_identity('dataset', spec.name)).one_or_none()
            if dataset is None:
                dataset = SqlaTable(uuid=object_identity('dataset', spec.name), table_name=spec.name,
                                    schema=target, database=database)
                db.session.add(dataset)
            dataset.table_name = spec.serving_table
            dataset.description = title_prefix + spec.label + '. Снимок после FINAL; обновление каждые 5 минут; время UTC.'
            if spec.name == 'player_activity':
                dataset.description = title_prefix + 'История игрока из шести готовых витрин. Без соединения фактов и повторного суммирования опыта; время UTC.'
            dataset.main_dttm_col = spec.time_column
            dataset.owners = [admin]
            attribute_owner(dataset, admin)
            db.session.flush()
            dataset.fetch_metadata()
            for name, (expression, label, column_type) in (calculated_columns or {}).get(spec.name, {}).items():
                column = next((c for c in dataset.columns if c.column_name == name), None)
                if column is None:
                    column = TableColumn(column_name=name, table=dataset)
                    db.session.add(column)
                column.expression = expression
                column.type = column_type
                column.verbose_name = label
                column.groupby = True
                column.filterable = True
                column.is_dttm = False
            existing = {metric.metric_name: metric for metric in dataset.metrics}
            for key, (expression, label) in spec.metrics.items():
                metric = existing.get(key)
                if metric is None:
                    metric = SqlMetric(metric_name=key, table=dataset)
                    db.session.add(metric)
                metric.expression = expression
                metric.verbose_name = (metric_labels or {}).get(spec.name, {}).get(key, label)
                metric.d3format = metric_format(key)
            for column in dataset.columns:
                if column.column_name in COLUMN_LABELS:
                    column.verbose_name = COLUMN_LABELS[column.column_name]
                if column.column_name in (column_labels or {}).get(spec.name, {}):
                    column.verbose_name = column_labels[spec.name][column.column_name]
                if column.column_name == spec.time_column:
                    column.is_dttm = True
                    column.verbose_name = 'Время · UTC'
            datasets[spec.name] = dataset
        db.session.flush()

        charts = {}
        for board in boards:
            for row in board.rows:
                for spec in row:
                    chart = db.session.query(Slice).filter_by(uuid=object_identity('chart', spec.key)).one_or_none()
                    if chart is None:
                        chart = Slice(uuid=object_identity('chart', spec.key))
                        db.session.add(chart)
                    chart.slice_name = (title_prefix if chart_title_prefix is None else chart_title_prefix) + spec.title
                    chart.datasource_id = datasets[spec.dataset].id
                    chart.datasource_type = 'table'
                    chart.datasource_name = spec.dataset
                    chart.viz_type = spec.kind
                    chart.description = title_prefix + (spec.description or board.note)
                    chart.owners = [admin]
                    attribute_owner(chart, admin)
                    chart.cache_timeout = 60 if board.show_period else 15
                    form, context = chart_payload(spec, chart.datasource_id, period=period, dataset_specs=dataset_specs)
                    if board.filter_column == 'player_id':
                        values = list((filter_defaults or {}).get(board.slug, []))
                        if values:
                            context['queries'][0]['filters'].append({'col':'player_id','op':'IN','val':values})
                        else:
                            context['queries'][0]['extras']['where'] = '1 = 0'
                    if spec.kind == 'table' and not spec.metrics:
                        # Raw-table JS treats strings as physical SQL identifiers.
                        # Keep calculated expressions explicit when the browser rebuilds queries.
                        definitions = (calculated_columns or {}).get(spec.dataset, {})
                        raw_columns = [({'expressionType': 'SQL', 'sqlExpression': definitions[name][0], 'label': name}
                                        if name in definitions else name) for name in spec.dimensions]
                        form['all_columns'] = raw_columns
                        context['queries'][0]['columns'] = raw_columns
                    if plain_legends and 'legendType' in form:
                        form['legendType'] = 'plain'
                        if spec.kind in ('echarts_timeseries_line', 'pie'):
                            form.update(legendOrientation='top', legendMargin=70)
                        if spec.key == 'cases-daily':
                            form.update(legendOrientation='right', legendMargin=145,
                                        x_axis_label_rotation=45, x_axis_title_margin=35)
                        if spec.key == 'player-daily':
                            form.update(legendOrientation='right', legendMargin=200)
                        if spec.kind == 'pie':
                            form.update(label_type='percent', labels_outside=True, show_labels_threshold=0,
                                        outerRadius=55, label_line=True,
                                        legendOrientation='right', legendMargin=145)
                        if spec.kind == 'echarts_timeseries_line' and len(spec.metrics) == 1 and not spec.dimensions:
                            form['show_legend'] = False
                        if spec.kind == 'echarts_timeseries_bar':
                            form.update(x_axis_force_categorical=True, show_legend=False)
                            context['queries'][0]['orderby'] = [[spec.dimensions[0], True]]
                    chart.params = json.dumps(form, ensure_ascii=False)
                    chart.query_context = json.dumps(context, ensure_ascii=False)
                    charts[spec.key] = chart
        db.session.flush()
        links = [f'[{board.title.split(" · ")[-1]}](/superset/dashboard/{board.slug}/)' for board in (navigation_boards or boards)]
        navigation = ' · '.join(links[:6]) + '\n\n' + ' · '.join(links[6:])
        for spec in boards:
            board = db.session.query(Dashboard).filter_by(uuid=object_identity('dashboard', spec.slug)).one_or_none()
            if board is None:
                if db.session.query(Dashboard).filter_by(slug=spec.slug).first():
                    raise RuntimeError('An unrelated dashboard already uses the managed slug: ' + spec.slug)
                board = Dashboard(uuid=object_identity('dashboard', spec.slug), slug=spec.slug)
                db.session.add(board)
            board.dashboard_title = spec.title
            board.description = spec.note
            if dashboard_css is not None:
                custom_css = re.sub(r'/\* VD_GUIDANCE_START \*/.*?/\* VD_GUIDANCE_END \*/',
                                    '', board.css or '', flags=re.S).strip()
                board.css = (custom_css + '\n\n' + dashboard_css).strip()
            board.published = True
            board.owners = [admin]
            attribute_owner(board, admin)
            board.slices = [charts[chart.key] for row in spec.rows for chart in row]
            board.position_json = json.dumps(layout(spec, charts, navigation), ensure_ascii=False)
            board.json_metadata = json.dumps({'native_filter_configuration': filters(spec, datasets, period=period, filter_defaults=filter_defaults),
                'color_scheme': 'supersetColors', 'refresh_frequency': 0, 'expanded_slices': {},
                'label_colors': label_colors or {}, 'timed_refresh_immune_slices': []}, ensure_ascii=False)
        db.session.commit()
        print(f'Provisioned {len(dataset_specs)} datasets, {len(charts)} charts and {len(boards)} dashboards')
        for board in boards:
            print('/superset/dashboard/' + board.slug + '/')
    return app


def main():
    target, user = provision_warehouse()
    app = provision_superset(target, user)
    from players import provision_player_dashboard
    from verify import client
    admin = client()
    try:
        provision_player_dashboard(admin, target, user, superset_app=app)
    finally:
        admin.close()


if __name__ == '__main__':
    main()
