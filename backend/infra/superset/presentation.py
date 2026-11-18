"""Native Superset form data, query contexts, filters and dashboard layout."""
from uuid import UUID, uuid5
import json
from catalog import DATASETS

NAMESPACE = UUID('9a73d163-6f0b-46ad-9aa4-a57f149488e5')
PERIOD = 'DATEADD(DATETIME("today"), -29, day) : DATEADD(DATETIME("today"), 1, day)'


def identity(kind, key):
    return uuid5(NAMESPACE, kind + ':' + key)


def chart_payload(spec, dataset_id, *, period=None, dataset_specs=None):
    time_column = next(s.time_column for s in (dataset_specs or DATASETS) if s.name == spec.dataset)
    period = (period or PERIOD) if time_column else 'No filter'
    form = {
        'datasource': f'{dataset_id}__table', 'viz_type': spec.kind,
        'granularity_sqla': time_column, 'time_range': period,
        'adhoc_filters': [], 'metrics': list(spec.metrics), 'groupby': list(spec.dimensions),
        'row_limit': 1000, 'color_scheme': 'supersetColors', 'show_legend': True,
        'rich_tooltip': True, 'y_axis_format': spec.format, 'number_format': spec.format,
        'show_empty_columns': False,
    }
    columns = list(spec.dimensions)
    for column, operator, values in spec.where_filters:
        form['adhoc_filters'].append({'expressionType':'SIMPLE','clause':'WHERE','subject':column,
                                     'operator':operator,'comparator':list(values)})
    operations = []
    if spec.kind == 'big_number_total':
        form.update(metric=spec.metrics[0], subheader=spec.subheader or spec.description or 'За выбранный период',
                    header_font_size=0.6, subheader_font_size=0.12)
    elif spec.kind == 'pie':
        form.update(metric=spec.metrics[0], donut=True, show_labels=True,
                    label_type='key_percent', legendType='scroll', legendOrientation='bottom')
    elif spec.kind == 'table':
        form.update(query_mode='aggregate' if spec.metrics else 'raw',
                    all_columns=[] if spec.metrics else columns, percent_metrics=[],
                    order_desc=True, page_length=10, server_pagination=False,
                    table_timestamp_format='%d.%m.%Y' if time_column else '%d.%m.%Y %H:%M:%S', include_search=True,
                    show_cell_bars=bool(spec.metrics),
                    column_config={})
    else:
        temporal = spec.kind == 'echarts_timeseries_line'
        axis = time_column if temporal else spec.dimensions[0]
        series = list(spec.dimensions) if temporal else list(spec.dimensions[1:])
        x_column = {'expressionType': 'SQL', 'sqlExpression': axis, 'label': axis,
                    'timeGrain': 'P1D', 'columnType': 'BASE_AXIS'} if temporal else axis
        columns = [x_column, *series]
        form.update(x_axis=axis, groupby=series, time_grain_sqla='P1D' if temporal else None,
                    x_axis_time_format='%d.%m', tooltipTimeFormat='%d.%m.%Y',
                    legendType='scroll', legendOrientation='bottom',
                    orientation='vertical', zoomable=False, markerEnabled=True,
                    show_value=False, truncateXAxis=True, x_axis_label_rotation=0)
        if spec.x_title or spec.y_title:
            form.update(x_axis_title=spec.x_title, y_axis_title=spec.y_title,
                        x_axis_title_margin=20, y_axis_title_margin=55, y_axis_title_position='Left')
        operations = [
            {'operation': 'pivot', 'options': {'index': [axis], 'columns': series,
                'aggregates': {name: {'operator': 'mean'} for name in spec.metrics},
                'drop_missing_columns': True}},
            {'operation': 'flatten'},
        ]
    query = {
        'time_range': period, 'granularity': time_column,
        'columns': columns, 'metrics': list(spec.metrics), 'filters': [],
        'orderby': [[spec.metrics[0], False]] if spec.metrics else [['dataset', True]], 'row_limit': 1000,
        'extras': {'having': '', 'where': ''}, 'is_timeseries': False,
        'post_processing': operations,
    }
    if spec.kind == 'echarts_timeseries_line':
        # Superset hashes the bucket alias. Ordering by the original timestamp
        # bypasses that bucket and violates ClickHouse GROUP BY; pivot sorts its index.
        query['orderby'] = []
    if spec.where_filters:
        query['filters'] = [{'col':column,'op':operator,'val':list(values)} for column,operator,values in spec.where_filters]
    if spec.order_by:
        query['orderby'] = [list(item) for item in spec.order_by]
        form['order_by_cols'] = [json.dumps(item) for item in spec.order_by]
    if spec.dataset == 'player_activity' and spec.kind == 'table':
        form['table_timestamp_format'] = '%d.%m.%Y %H:%M:%S'
        if not spec.metrics:
            query['metrics'] = None
    context = {'datasource': {'id': dataset_id, 'type': 'table'}, 'force': False,
               'queries': [query], 'form_data': form, 'result_format': 'json', 'result_type': 'full'}
    return form, context


def layout(board, charts, navigation):
    positions = {
        'DASHBOARD_VERSION_KEY': 'v2',
        'ROOT_ID': {'id': 'ROOT_ID', 'type': 'ROOT', 'children': ['GRID_ID']},
        'GRID_ID': {'id': 'GRID_ID', 'type': 'GRID', 'children': [], 'parents': ['ROOT_ID']},
    }
    intro = 'ROW-INTRO'
    positions['GRID_ID']['children'].append(intro)
    positions[intro] = {'id': intro, 'type': 'ROW', 'children': ['MARKDOWN-INTRO'],
                        'parents': ['ROOT_ID', 'GRID_ID'], 'meta': {'background': 'BACKGROUND_TRANSPARENT'}}
    positions['MARKDOWN-INTRO'] = {'id': 'MARKDOWN-INTRO', 'type': 'MARKDOWN', 'children': [],
        'parents': ['ROOT_ID', 'GRID_ID', intro],
        'meta': {'width': 12, 'height': board.intro_height, 'code': navigation + '\n\n' + board.note
                 + ('\n\nТаблицы и разрезы показывают до 1000 групп. Сузьте период или фильтр для детализации.'
                    if board.show_period and board.show_limit_note else '')}}
    if board.intro_sections:
        # Keep navigation, page purpose and reading guidance visually distinct.
        positions['MARKDOWN-INTRO']['meta'].update(code=navigation, height=12)
        positions['GRID_ID']['children'].append('ROW-SUMMARY')
        positions['ROW-SUMMARY'] = {'id':'ROW-SUMMARY', 'type':'ROW', 'children':['MARKDOWN-SUMMARY'],
            'parents':['ROOT_ID','GRID_ID'], 'meta':{'background':'BACKGROUND_TRANSPARENT'}}
        positions['MARKDOWN-SUMMARY'] = {'id':'MARKDOWN-SUMMARY', 'type':'MARKDOWN', 'children':[],
            'parents':['ROOT_ID','GRID_ID','ROW-SUMMARY'], 'meta':{'width':12,'height':12,'code':board.note}}
        if not all(s.kind == 'big_number_total' for s in board.rows[0]):
            positions['GRID_ID']['children'].append('ROW-READING')
        positions['ROW-READING'] = {'id':'ROW-READING','type':'ROW','children':[],
            'parents':['ROOT_ID','GRID_ID'],'meta':{'background':'BACKGROUND_TRANSPARENT'}}
        for index, (title, text) in enumerate(board.intro_sections):
            key = f'MARKDOWN-READING-{index}'
            positions['ROW-READING']['children'].append(key)
            positions[key] = {'id':key,'type':'MARKDOWN','children':[],
                'parents':['ROOT_ID','GRID_ID','ROW-READING'],
                'meta':{'width':12//len(board.intro_sections),'height':28,'code':'### '+title+'\n\n'+text}}
    if board.show_period and board.show_limit_note:
        positions['MARKDOWN-INTRO']['meta']['code'] += ('\n\nОшибка загрузки не означает ноль. Проверьте «Обновление BI»; '
            'при повторной ошибке передайте администратору код обращения из сообщения.')
    for index, row in enumerate(board.rows):
        for row_index, title in board.row_headers:
            if row_index == index:
                header_row = f'ROW-HEADING-{index}'
                header = f'MARKDOWN-HEADING-{index}'
                positions['GRID_ID']['children'].append(header_row)
                positions[header_row] = {'id':header_row,'type':'ROW','children':[header],
                    'parents':['ROOT_ID','GRID_ID'],'meta':{'background':'BACKGROUND_TRANSPARENT'}}
                positions[header] = {'id':header,'type':'MARKDOWN','children':[],
                    'parents':['ROOT_ID','GRID_ID',header_row],'meta':{'width':12,'height':9,'code':'### '+title}}
        row_id = f'ROW-{index}'
        positions['GRID_ID']['children'].append(row_id)
        positions[row_id] = {'id': row_id, 'type': 'ROW', 'children': [],
            'parents': ['ROOT_ID', 'GRID_ID'], 'meta': {'background': 'BACKGROUND_TRANSPARENT'}}
        for spec in row:
            chart = charts[spec.key]
            key = 'CHART-' + spec.key
            positions[row_id]['children'].append(key)
            positions[key] = {'id': key, 'type': 'CHART', 'children': [],
                'parents': ['ROOT_ID', 'GRID_ID', row_id],
                'meta': {'chartId': chart.id, 'uuid': str(chart.uuid), 'sliceName': spec.title,
                         'width': 12 // len(row),
                         'height': spec.height or (22 if all(s.kind == 'big_number_total' for s in row) else 43)}}
        if any(s.guide for s in row):
            guide_row = f'ROW-GUIDE-{index}'
            positions['GRID_ID']['children'].append(guide_row)
            positions[guide_row] = {'id':guide_row,'type':'ROW','children':[],
                'parents':['ROOT_ID','GRID_ID'],'meta':{'background':'BACKGROUND_TRANSPARENT'}}
            for spec in row:
                key = 'MARKDOWN-GUIDE-'+spec.key
                positions[guide_row]['children'].append(key)
                positions[key] = {'id':key,'type':'MARKDOWN','children':[],
                    'parents':['ROOT_ID','GRID_ID',guide_row],
                    'meta':{'width':12//len(row),'height':spec.guide_height or (26 if len(row)>1 else 20),'code':spec.guide}}
        if index == board.reading_after_row and board.intro_sections and all(s.kind == 'big_number_total' for s in board.rows[0]):
            positions['GRID_ID']['children'].append('ROW-READING')
    if board.footer:
        positions['GRID_ID']['children'].append('ROW-HELP')
        positions['ROW-HELP'] = {'id': 'ROW-HELP', 'type': 'ROW', 'children': ['MARKDOWN-HELP'],
            'parents': ['ROOT_ID', 'GRID_ID'], 'meta': {'background': 'BACKGROUND_TRANSPARENT'}}
        positions['MARKDOWN-HELP'] = {'id': 'MARKDOWN-HELP', 'type': 'MARKDOWN', 'children': [],
            'parents': ['ROOT_ID', 'GRID_ID', 'ROW-HELP'], 'meta': {'width': 12, 'height': board.footer_height, 'code': board.footer}}
    return positions


def filters(board, datasets, *, period=None, filter_defaults=None):
    if not board.show_period:
        return []
    period = period or PERIOD
    result = [{'id': 'NATIVE_FILTER-' + board.slug + '-time',
        'name': 'Период получения · UTC' if board.filter_dataset == 'event_delivery' else 'Период · UTC',
        'filterType': 'filter_time', 'targets': [{}], 'type': 'NATIVE_FILTER',
        'cascadeParentIds': [], 'controlValues': {},
        'scope': {'rootPath': ['ROOT_ID'], 'excluded': []},
        'defaultDataMask': {'extraFormData': {'time_range': period},
                            'filterState': {'value': period}}}]
    if board.filter_column:
        result.append({'id': 'NATIVE_FILTER-' + board.slug + '-category',
            'name': {'ruleset_id': 'Версия правил', 'case_code': 'Кейс', 'offer_id': 'Предложение',
                     'source': 'Сервис', 'case_name': 'Кейс', 'offer_name': 'Предложение',
                     'source_name': 'Сервис','player_id':'Игрок · ID'}[board.filter_column],
            'filterType': 'filter_select', 'type': 'NATIVE_FILTER',
            'targets': [{'datasetId': datasets[board.filter_dataset].id,
                         'column': {'name': board.filter_column}}],
            'cascadeParentIds': ['NATIVE_FILTER-' + board.slug + '-time'],
            'controlValues': {'multiSelect': True, 'enableEmptyFilter': False,
                                                      'searchAllOptions': True},
            'scope': {'rootPath': ['ROOT_ID'], 'excluded': []},
            'defaultDataMask': {'extraFormData': {}, 'filterState': {}}})
        if board.filter_column == 'player_id':
            values = list((filter_defaults or {}).get(board.slug, []))
            result[-1]['controlValues'].update(multiSelect=False, enableEmptyFilter=True,
                defaultToFirstItem=False, creatable=True)
            result[-1]['defaultDataMask'] = {'extraFormData':{'filters':[{'col':'player_id','op':'IN','val':values}]},
                'filterState':{'value':values,'label':values[0] if values else 'Выберите игрока'}}
            if not values:
                result[-1]['defaultDataMask']['extraFormData'] = {'adhoc_filters':[
                    {'expressionType':'SQL','clause':'WHERE','sqlExpression':'1 = 0'}]}
    return result
