"""Version-controlled BI definitions. All source identities are deduplicated first."""
from dataclasses import dataclass


@dataclass(frozen=True)
class Dataset:
    name: str
    label: str
    sql: str
    metrics: dict
    time_column: str | None = 'occurred_at'

    @property
    def serving_table(self):
        return self.name + '_snapshot' if self.sql else self.name


DATASETS = [
    Dataset('runs', 'Подтверждённые забеги', """
        SELECT run_id, player_id, occurred_at, toDate(occurred_at) AS day,
            ruleset_id, content_release_id, outcome, church_level,
            hero_kills, tower_kills, building_upgrades,
            xp_granted, isNotNull(xp_granted) AS xp_known,
            toDecimal128(ifNull(xp_granted, 0), 0) AS known_xp
        FROM {source}.fact_run_settlements FINAL
    """, {
        'runs': ('count()', 'Забеги'),
        'players': ('uniqExact(player_id)', 'Участники забегов'),
        'victories': ("countIf(outcome = 'victory')", 'Победы'),
        'win_rate': ("countIf(outcome = 'victory') / nullIf(count(), 0)", 'Доля побед'),
        'known_xp': ('sum(known_xp)', 'Известный начисленный XP'),
        'xp_coverage': ('countIf(xp_known = 1) / nullIf(count(), 0)', 'Забеги с данными XP'),
        'missing_xp_runs': ('countIf(xp_known = 0)', 'Забеги без данных XP'),
        'hero_kills': ('sum(toDecimal128(hero_kills, 0))', 'Убийства героем'),
        'tower_kills': ('sum(toDecimal128(tower_kills, 0))', 'Убийства башнями'),
        'upgrades': ('sum(toDecimal128(building_upgrades, 0))', 'Улучшения построек'),
        'avg_hero': ('avg(hero_kills)', 'Убийства героем / забег'),
        'avg_tower': ('avg(tower_kills)', 'Убийства башнями / забег'),
        'avg_upgrades': ('avg(building_upgrades)', 'Улучшения / забег'),
    }),
    Dataset('xp', 'Начисления XP', """
        SELECT event_id, player_id, occurred_at, toDate(occurred_at) AS day,
            run_id, ruleset_id, source_type, level_after,
            toDecimal128(xp_delta, 0) AS xp_delta,
            toDecimal128(hero_kill_xp, 0) AS hero_xp,
            toDecimal128(tower_kill_xp, 0) AS tower_xp,
            toDecimal128(tycoon_upgrade_xp, 0) AS tycoon_xp,
            toDecimal128(victory_xp, 0) AS victory_xp
        FROM {source}.fact_xp_transactions FINAL
    """, {
        'xp': ('sum(xp_delta)', 'Начисленный XP'),
        'hero_xp': ('if(count(hero_xp) = 0, NULL, sum(hero_xp))', 'XP за убийства героем'),
        'tower_xp': ('if(count(tower_xp) = 0, NULL, sum(tower_xp))', 'XP за убийства башнями'),
        'tycoon_xp': ('if(count(tycoon_xp) = 0, NULL, sum(tycoon_xp))', 'XP за улучшения'),
        'victory_xp': ('if(count(victory_xp) = 0, NULL, sum(victory_xp))', 'XP за победы'),
        'transactions': ('count()', 'Начисления'),
    }),
    Dataset('level_ups', 'Переходы уровней', """
        SELECT event_id, player_id, occurred_at, toDate(occurred_at) AS day,
            run_id, old_level, new_level,
            toInt64(new_level) - toInt64(old_level) AS levels_gained
        FROM {source}.fact_player_level_ups FINAL
    """, {
        'transitions': ('count()', 'Переходы уровней'),
        'levels': ('sum(levels_gained)', 'Получено уровней'),
        'players': ('uniqExact(player_id)', 'Игроки с повышением уровня'),
    }),
    Dataset('level_gifts', 'Полученные подарки', """
        SELECT claim_id, player_id, assumeNotNull(claimed_at) AS occurred_at,
            toDate(claimed_at) AS day, rewarded_level, reward_bundle_id
        FROM {source}.fact_level_gift_claims FINAL
        WHERE claim_status = 'CLAIMED' AND claimed_at IS NOT NULL
    """, {
        'claims': ('count()', 'Полученные подарки'),
        'players': ('uniqExact(player_id)', 'Получатели подарков'),
    }),
    Dataset('case_openings', 'Завершённые кейсы', """
        SELECT opening_id, player_id, completed_at AS occurred_at, toDate(completed_at) AS day,
            case_code, ruleset_id, payment_mode, rarity, pity_before, pity_after,
            guaranteed_triggered, entry_index
        FROM {source}.fact_case_settlements FINAL
    """, {
        'openings': ('count()', 'Открытые кейсы'),
        'players': ('uniqExact(player_id)', 'Игроки с открытием'),
        'guarantees': ('countIf(guaranteed_triggered = 1)', 'Сработал гарант'),
        'guarantee_rate': ('countIf(guaranteed_triggered = 1) / nullIf(count(), 0)', 'Доля гаранта'),
        'avg_pity': ('avg(pity_before)', 'Средний счётчик до открытия'),
        'per_player': ('count() / nullIf(uniqExact(player_id), 0)', 'Открытий на участника'),
    }),
    Dataset('shop_purchases', 'Покупки за престиж', """
        SELECT purchase_id, operation_id, player_id, completed_at AS occurred_at,
            toDate(completed_at) AS day, offer_id, catalog_version,
            currency_code, toDecimal128(charged_amount, 0) AS charged_amount,
            weapon_configuration_id
        FROM {source}.fact_shop_purchases FINAL
    """, {
        'purchases': ('count()', 'Завершённые покупки'),
        'players': ('uniqExact(player_id)', 'Покупатели'),
        'prestige': ("sumIf(charged_amount, currency_code = 'PRESTIGE')", 'Потраченный престиж'),
        'average_price': ("avgIf(charged_amount, currency_code = 'PRESTIGE')", 'Средняя цена, включая бесплатные'),
        'paid_average': ("avgIf(charged_amount, currency_code = 'PRESTIGE' AND charged_amount > 0)", 'Средняя платная покупка · PRESTIGE'),
        'paid_players': ("uniqExactIf(player_id, currency_code = 'PRESTIGE' AND charged_amount > 0)", 'Игроки с расходом престижа'),
        'paid_purchases': ("countIf(charged_amount > 0 AND currency_code = 'PRESTIGE')", 'Покупки за престиж'),
        'free_purchases': ('countIf(charged_amount = 0)', 'Бесплатные выдачи'),
        'free_rate': ('countIf(charged_amount = 0) / nullIf(count(), 0)', 'Доля бесплатных выдач'),
    }),
    Dataset('event_delivery', 'Доставка событий', """
        SELECT event_id, occurred_at, received_at, toDate(received_at) AS day,
            event_name, source, schema_version,
            dateDiff('millisecond', occurred_at, received_at) / 1000.0 AS lag_seconds
        FROM {source}.raw_game_events FINAL
        WHERE source IN ('game_backend', 'shop') AND event_name IN (
            'run.completed', 'xp.granted', 'player.level_up',
            'level_gift.claimed', 'case.opened', 'shop.purchase_confirmed')
    """, {
        'events': ('count()', 'События'),
        'event_types': ('uniqExact(event_name)', 'Типы событий'),
        'average_lag': ('avgIf(lag_seconds, lag_seconds >= 0)', 'Средняя неотрицательная задержка, с'),
        'late_events': ('countIf(lag_seconds > 300)', 'Задержка более 5 минут'),
        'clock_skew': ('countIf(lag_seconds < 0)', 'Отрицательная задержка'),
    }, time_column='received_at'),
]

BUSINESS_DATASETS = tuple(DATASETS)
DATASETS.append(Dataset('refresh_status', 'Состояние витрин BI', '', {}, time_column=None))

COLUMN_LABELS = {
    'ruleset_id': 'Версия правил', 'content_release_id': 'Версия контента',
    'outcome': 'Исход', 'church_level': 'Уровень церкви', 'rewarded_level': 'Уровень награды',
    'reward_bundle_id': 'ID набора наград', 'case_code': 'Кейс', 'payment_mode': 'Способ оплаты',
    'rarity': 'Редкость', 'offer_id': 'Предложение', 'catalog_version': 'Версия каталога',
    'currency_code': 'Валюта', 'source': 'Сервис', 'event_name': 'Событие', 'schema_version': 'Версия схемы',
    'dataset': 'Витрина', 'health': 'Состояние данных', 'last_success': 'Последний успех · UTC',
    'age_seconds': 'Возраст · с', 'next_refresh': 'Следующий запуск · UTC',
    'rows_written': 'Записано строк¹', 'duration_ms': 'Длительность успеха · мс', 'refresh_state': 'Состояние задачи',
    'recovery_hint': 'Что делать', 'health_code': 'Код состояния', 'last_attempt': 'Последняя попытка · UTC',
}


@dataclass(frozen=True)
class Chart:
    key: str
    title: str
    dataset: str
    kind: str
    metrics: tuple
    dimensions: tuple = ()
    format: str = ',.0f'
    description: str = ''
    subheader: str = ''
    x_title: str = ''
    y_title: str = ''
    height: int = 0
    guide: str = ''
    where_filters: tuple = ()
    order_by: tuple = ()
    guide_height: int = 0


@dataclass(frozen=True)
class Board:
    slug: str
    title: str
    note: str
    rows: tuple
    filter_column: str | None = None
    filter_dataset: str | None = None
    show_period: bool = True
    intro_height: int = 22
    footer: str = ''
    show_limit_note: bool = True
    intro_sections: tuple = ()
    footer_height: int = 16
    row_headers: tuple = ()
    reading_after_row: int = 0


def kpi(key, title, dataset, metric, format=',.0f', description=''):
    return Chart(key, title, dataset, 'big_number_total', (metric,), format=format, description=description)


def trend(key, title, dataset, metrics, dimensions=()):
    format = ',.1f' if any(name.startswith('avg_') for name in metrics) else ',.0f'
    return Chart(key, title, dataset, 'echarts_timeseries_line', tuple(metrics), tuple(dimensions), format=format)


def group(key, title, dataset, metric, dimensions, kind='echarts_timeseries_bar'):
    return Chart(key, title, dataset, kind, (metric,), tuple(dimensions))


def table(key, title, dataset, metrics, dimensions):
    return Chart(key, title, dataset, 'table', tuple(metrics), tuple(dimensions))


BOARDS = [
    Board('vatican-runs', 'VaticanDefendor · Забеги',
        'Результаты подтверждённых забегов; время UTC. Участники — уникальные игроки с забегом '
        'за весь выбранный период, не DAU приложения. Известный XP не включает забеги без данных XP. '
        'Пустой график означает отсутствие фактов. Витрины обновляются каждые 5 минут; кэш до 60 секунд.', (
        (kpi('runs-total', 'Завершённые забеги', 'runs', 'runs'),
         kpi('runs-players', 'Участники забегов', 'runs', 'players'),
         kpi('runs-win-rate', 'Доля побед', 'runs', 'win_rate', '.1%'),
         kpi('runs-known-xp', 'Известный XP', 'runs', 'known_xp')),
        (trend('runs-daily', 'Забеги по дням и исходам', 'runs', ['runs'], ['outcome']),
         group('runs-outcomes', 'Распределение исходов', 'runs', 'runs', ['outcome'], 'pie')),
    ), 'ruleset_id', 'runs'),
    Board('vatican-balance', 'VaticanDefendor · Баланс',
        'Средние убийства и улучшения на один подтверждённый забег, включая нулевые значения. '
        'Средние пересчитываются по забегам за весь период, а не усредняются по дням. '
        'Число забегов в таблице помогает оценить объём наблюдений. Время UTC.', (
        (kpi('balance-hero', 'Убийства героем / забег', 'runs', 'avg_hero', ',.1f'),
         kpi('balance-tower', 'Убийства башнями / забег', 'runs', 'avg_tower', ',.1f'),
         kpi('balance-upgrades', 'Улучшения / забег', 'runs', 'avg_upgrades', ',.1f')),
        (group('runs-church', 'Достигнутый уровень церкви', 'runs', 'runs', ['church_level']),
         trend('balance-daily', 'Средние убийства на забег по дням', 'runs', ['avg_hero', 'avg_tower'])),
        (table('runs-rulesets', 'Версии правил · объём и результат', 'runs',
               ['runs', 'players', 'win_rate', 'avg_hero', 'avg_tower', 'avg_upgrades'], ['ruleset_id']),),
    ), 'ruleset_id', 'runs'),
    Board('vatican-progression', 'VaticanDefendor · Прогрессия',
        'XP начислений не складывается с XP забегов: это разные представления одних действий. '
        'Полнота XP — доля забегов с известным начислением, включая нулевое. Пропуски источников XP '
        'не подменяются нулями. Переход 1 → 3 — один переход и два уровня. Время UTC.', (
        (kpi('xp-total', 'Известный XP забегов', 'runs', 'known_xp'),
         kpi('xp-coverage', 'Забеги с данными XP', 'runs', 'xp_coverage', '.1%'),
         kpi('levels-gained', 'Получено уровней', 'level_ups', 'levels'),
         kpi('xp-transactions', 'Начисления XP', 'xp', 'transactions')),
        (trend('xp-daily', 'Известный XP по источникам', 'xp', ['hero_xp', 'tower_xp', 'tycoon_xp', 'victory_xp']),
         trend('levels-daily', 'Переходы и полученные уровни', 'level_ups', ['transitions', 'levels'])),
        (table('xp-completeness', 'Полнота XP по версии правил', 'runs',
               ['runs', 'missing_xp_runs', 'xp_coverage', 'known_xp'], ['ruleset_id']),),
    )),
    Board('vatican-gifts', 'VaticanDefendor · Подарки',
        'Только полученные подарки: статус CLAIMED и известное время claimed_at (UTC). '
        'Ожидающие выдачи не учитываются. Число получателей считается за весь период; '
        'отношение подарков к повышениям уровня не является конверсией без связи конкретных игроков и когорт.', (
        (kpi('gifts-total', 'Полученные подарки', 'level_gifts', 'claims'),
         kpi('gifts-players', 'Получатели подарков', 'level_gifts', 'players')),
        (trend('gifts-daily', 'Получение подарков по дням', 'level_gifts', ['claims']),
         group('gifts-level', 'За какой уровень получен подарок', 'level_gifts', 'claims', ['rewarded_level'])),
        (table('gifts-bundles', 'Наборы наград', 'level_gifts', ['claims', 'players'], ['reward_bundle_id', 'rewarded_level']),),
    )),
    Board('vatican-cases', 'VaticanDefendor · Кейсы и гарант',
        'Только полностью завершённые открытия; время UTC. Доля срабатываний гаранта — наблюдаемый '
        'результат, а не настроенная вероятность выпадения. Способ оплаты не содержит сумму списания. '
        'Средний счётчик — pity_before до открытия; неподтверждённые дубликаты наград не рассчитываются.', (
        (kpi('cases-total', 'Открытые кейсы', 'case_openings', 'openings'),
         kpi('cases-players', 'Игроки с открытием', 'case_openings', 'players'),
         kpi('cases-guarantee', 'Срабатывания гаранта · %', 'case_openings', 'guarantee_rate', '.1%'),
         kpi('cases-per-player', 'Открытий на участника', 'case_openings', 'per_player', ',.1f')),
        (trend('cases-daily', 'Открытия по дням и редкости', 'case_openings', ['openings'], ['rarity']),
         group('cases-rarity', 'Полученная редкость', 'case_openings', 'openings', ['rarity'], 'pie')),
        (table('cases-catalog', 'Кейсы · оплата, гарант и счётчик', 'case_openings',
               ['openings', 'players', 'guarantees', 'avg_pity'], ['case_code', 'payment_mode']),),
    ), 'case_code', 'case_openings'),
    Board('vatican-shop', 'VaticanDefendor · Магазин за престиж',
        'PRESTIGE — внутриигровая валюта, не денежная выручка. Только Completed после wallet и inventory. '
        'Общее число включает бесплатные выдачи; игроки с расходом и средняя платная покупка — только '
        'PRESTIGE > 0. Средние и доли пересчитываются за весь выбранный период. Время UTC.', (
        (kpi('shop-total', 'Покупки и бесплатные выдачи', 'shop_purchases', 'purchases'),
         kpi('shop-players', 'Игроки с расходом престижа', 'shop_purchases', 'paid_players'),
         kpi('shop-prestige', 'Потраченный престиж', 'shop_purchases', 'prestige'),
         kpi('shop-price', 'Средняя платная покупка', 'shop_purchases', 'paid_average', ',.1f', 'PRESTIGE; бесплатные исключены')),
        (trend('shop-daily', 'Покупки за престиж и бесплатные выдачи', 'shop_purchases', ['paid_purchases', 'free_purchases']),
         trend('shop-spend', 'Расход престижа по дням', 'shop_purchases', ['prestige'])),
        (table('shop-catalog', 'Предложения · объём, расход и бесплатные выдачи', 'shop_purchases',
               ['purchases', 'paid_players', 'prestige', 'paid_average', 'free_rate'], ['offer_id', 'catalog_version', 'currency_code']),),
    ), 'offer_id', 'shop_purchases'),
    Board('vatican-events', 'VaticanDefendor · Доставка событий',
        'Период — время получения received_at в ClickHouse (UTC), а не время игрового действия. '
        'Одна логическая запись события после дедупликации; повторная доставка обновляет её received_at. '
        'Средняя задержка исключает отрицательные значения; рассогласование часов показано отдельно. '
        'Это задержка проекции, не длительность HTTP-запроса. Несколько событий могут относиться к одному забегу.', (
        (kpi('events-total', 'Полученные бизнес-события', 'event_delivery', 'events'),
         kpi('events-lag', 'Средняя задержка · с', 'event_delivery', 'average_lag', ',.1f', 'Без отрицательных значений'),
         kpi('events-late', 'Задержка более 5 минут', 'event_delivery', 'late_events'),
         kpi('events-clock', 'Отрицательная задержка', 'event_delivery', 'clock_skew')),
        (trend('events-daily', 'Получение событий по дням и сервисам', 'event_delivery', ['events'], ['source']),),
        (table('events-contracts', 'Поток по контрактам', 'event_delivery',
               ['events', 'average_lag', 'late_events', 'clock_skew'], ['source', 'event_name', 'schema_version']),),
    ), 'source', 'event_delivery'),
    Board('vatican-bi-health', 'VaticanDefendor · Обновление BI',
        'Служебная страница без фильтра периода. Витрины перестраиваются каждые 5 минут, кэш графиков '
        'до 60 секунд. Здесь кэш 15 секунд. Более 10 минут без успешного обновления — устаревшие данные. '
        'Ошибка сохраняет последнюю успешную копию; ноль строк означает пустой источник только при успешном обновлении. '
        'Снимки независимы: межсервисные сравнения не являются единой транзакцией. '
        'При отсутствии подтверждённого обновления пустая копия не доказывает отсутствие игровых действий. '
        'Колонка «Что делать» объясняет восстановление; техническая причина доступна администратору в журнале ClickHouse.', (
        (Chart('bi-freshness', 'Состояние всех семи витрин', 'refresh_status', 'table', (),
               ('dataset', 'health', 'last_success', 'age_seconds', 'next_refresh', 'recovery_hint'), height=62),),
    ), show_period=False),
]
