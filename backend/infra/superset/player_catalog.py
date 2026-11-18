"""A single player dimension shared by every chart; facts are unioned, never joined."""
from catalog import Board, Chart, Dataset
from serving import identifier


PLAYER_DATASET = Dataset('player_activity','Действия игрока','',{
    'records':('count()','Записи действий'),
    'active_days':('uniqExact(toDate(occurred_at))','Дней с действиями'),
    'runs':("countIf(activity_type='run')",'Забеги'),
    'win_rate':("countIf(activity_type='run' AND outcome_name='Победа') / nullIf(countIf(activity_type='run'),0)",'Победы, %'),
    'xp':("ifNull(sumIf(xp_delta,activity_type='xp'),0)",'Начислено XP'),
    'levels':("ifNull(sumIf(levels_gained,activity_type='level'),0)",'Получено уровней'),
    'purchases':("countIf(activity_type='purchase')",'Покупки и бесплатные выдачи'),
    'prestige':("ifNull(sumIf(charged_amount,activity_type='purchase' AND currency_code='PRESTIGE'),0)",'Расход, престиж'),
    'cases':("countIf(activity_type='case')",'Открытые кейсы'),
    'gifts':("countIf(activity_type='gift')",'Полученные подарки'),
    'hero_kills':('ifNull(sum(hero_kills),0)','Убийств героем'),
    'tower_kills':('ifNull(sum(tower_kills),0)','Убийств башнями'),
    'upgrades':('ifNull(sum(building_upgrades),0)','Улучшений построек'),
})

COLUMN_LABELS = {'player_id':'ID игрока','activity_label':'Действие','detail':'Подробности',
    'record_id':'ID записи','outcome_name':'Исход','church_level':'Уровень церкви',
    'hero_kills':'Герой: убийства','tower_kills':'Башни: убийства','building_upgrades':'Улучшения',
    'run_xp':'Опыт за забег, XP','offer_name':'Предложение','currency_name':'Валюта',
    'charged_amount':'Списано','reference_id':'ID покупки'}


def translated(expression, mapping):
    keys=', '.join(repr(k) for k in mapping)
    values=', '.join(repr(v) for v in mapping.values())
    return f"transform({expression}, [{keys}], [{values}], concat('Код: ',toString({expression})))"


def activity_sql(target, demo=False):
    bi=identifier(target)
    nullable = {
        'outcome_name':'String','church_level':'UInt32','hero_kills':'Decimal(38,0)',
        'tower_kills':'Decimal(38,0)','building_upgrades':'Decimal(38,0)','run_xp':'Decimal(38,0)',
        'xp_delta':'Decimal(38,0)','levels_gained':'Int64','offer_name':'String','currency_name':'String',
        'currency_code':'String','charged_amount':'Decimal(38,0)',
    }
    common={'player_id':'toString(player_id)','occurred_at':'occurred_at'}
    defaults={name:f"CAST(NULL AS Nullable({kind}))" for name,kind in nullable.items()}

    def branch(table, kind, label, key, detail, fields=None):
        values={**common,'record_id':f"concat('{kind}:',toString({key}))",'reference_id':f'toString({key})',
            'activity_type':repr(kind),'activity_label':repr(label),'detail':detail,**defaults,**(fields or {})}
        return 'SELECT '+', '.join(expression+' AS '+identifier(name) for name,expression in values.items())+f' FROM {bi}.{identifier(table)}'

    outcome=translated('outcome',{'victory':'Победа','defeat':'Поражение'})
    rarity=translated('rarity',{'COMMON':'Обычная','RARE':'Редкая','EPIC':'Эпическая','LEGENDARY':'Легендарная'})
    offer='offer_id'
    case='case_code'
    if demo:
        offer=translated('offer_id',{'starter_sword':'Стартовый меч','guardian_blade':'Клинок стража',
                                    'archer_pack':'Набор лучника','weekly_gift':'Недельный подарок'})
        case=translated('case_code',{'recruit':'Новобранец','guardian':'Страж','legend':'Легенда'})
    currency="if(currency_code='PRESTIGE','Престиж',currency_code)"
    branches=[
        branch('runs_snapshot','run','Забег','run_id',
            f"concat({outcome}, '; церковь ',toString(church_level),'; герой ',toString(hero_kills),'; башни ',toString(tower_kills),'; улучшения ',toString(building_upgrades))",
            {'outcome_name':outcome,'church_level':'toUInt32(church_level)',
             'hero_kills':'toDecimal128(hero_kills,0)','tower_kills':'toDecimal128(tower_kills,0)',
             'building_upgrades':'toDecimal128(building_upgrades,0)','run_xp':'toDecimal128(xp_granted,0)'}),
        branch('xp_snapshot','xp','Начисление XP','event_id',
            "concat('+',toString(xp_delta),' XP; уровень ',toString(level_after))",{'xp_delta':'xp_delta'}),
        branch('level_ups_snapshot','level','Повышение уровня','event_id',
            "concat(toString(old_level),' → ',toString(new_level),' (+',toString(levels_gained),')')",{'levels_gained':'levels_gained'}),
        branch('level_gifts_snapshot','gift','Подарок за уровень','claim_id',
            "concat('За уровень ',toString(rewarded_level),'; набор ',toString(reward_bundle_id))"),
        branch('case_openings_snapshot','case','Открытие кейса','opening_id',
            f"concat({case},'; ',{rarity},'; гарант ',if(guaranteed_triggered=1,'да','нет'),'; счётчик до ',toString(pity_before))"),
        branch('shop_purchases_snapshot','purchase','Покупка / выдача','purchase_id',
            f"concat({offer},'; ',if(charged_amount=0,'бесплатно',concat(toString(charged_amount),' ',{currency})))",
            {'offer_name':offer,'currency_name':currency,'currency_code':'currency_code','charged_amount':'charged_amount'}),
    ]
    return '\nUNION ALL\n'.join(branches)


def board(demo=False, reference_at=None):
    dataset=PLAYER_DATASET.name

    def kpi(key,title,metric,caption,description,format=',.0f'):
        return Chart('player-'+key,title,dataset,'big_number_total',(metric,),format=format,
                     subheader=caption,description=description)

    def history(key,title,columns,kind=None,description=''):
        return Chart('player-'+key,title,dataset,'table',(),tuple(columns),description=description,
            guide=description,guide_height=14,height=55,where_filters=(('activity_type','IN',(kind,)),) if kind else (),
            order_by=(('occurred_at',False),('record_id',True)))

    rows=(
        (kpi('runs','Завершённые забеги','runs','Подтверждённые результаты','Количество завершённых забегов выбранного игрока за период. Незавершённые попытки исключены.'),
         kpi('wins','Доля побед','win_rate','Победы ÷ забеги','Победы выбранного игрока делятся на его завершённые забеги. Без забегов доля не определена.','.1%'),
         kpi('xp','Начислено опыта','xp','XP из записей о начислениях','Сумма подтверждённых начислений опыта. Опыт из результатов забегов повторно не прибавляется.'),
         kpi('levels','Получено уровней','levels','Сумма прибавок уровня','Переход 1 → 3 даёт два уровня. Это прибавка за период, а не текущий уровень аккаунта.')),
        (kpi('purchases','Выдачи магазина','purchases','Платные и бесплатные','Успешные покупки и бесплатные выдачи выбранного игрока. Неуспешные и незавершённые операции исключены.'),
         kpi('prestige','Расход в магазине','prestige','Престиж, ед.','Сумма списанного престижа по завершённым покупкам. Другие валюты и списания вне магазина исключены. Это не текущий баланс кошелька и не денежная выручка.'),
         kpi('cases','Открытые кейсы','cases','Оплата и выдача завершены','Количество полностью завершённых открытий кейсов выбранного игрока.'),
         kpi('gifts','Полученные подарки','gifts','Получение подтверждено','Количество подарков по фактической дате получения. Ожидающие подарки исключены.')),
        (Chart('player-summary','Игрок и действия в забегах',dataset,'table',
            ('active_days','hero_kills','tower_kills','upgrades'),('player_id',),height=20,guide_height=14,
            description='Полный ID выбранного игрока. Дни активности считаются по шести видам записей, убийства и улучшения — только по завершённым забегам.',
            guide='**Полный ID** помогает проверить, чей профиль открыт. Дни активности — разные дни UTC хотя бы с одной записью. Убийства и улучшения суммируются только по забегам; записи опыта их не дублируют.'),),
        (Chart('player-daily','Активность игрока по дням',dataset,'echarts_timeseries_line',('records',),('activity_label',),
            x_title='День · UTC',y_title='Записи, шт.',height=43,guide_height=14,
            description='Количество записей каждого типа по дням UTC. Забег и начисление XP — разные связанные записи, а не два независимых игровых действия.',
            guide='**Линия — тип записи.** Один забег может породить запись результата, начисление опыта и повышение уровня. Их количество не следует трактовать как число независимых действий или кликов.'),),
        (Chart('player-spend','Покупки: расход престижа по дням',dataset,'echarts_timeseries_line',('prestige',),
            x_title='День · UTC',y_title='Престиж, ед.',height=32,guide_height=14,
            where_filters=(('activity_type','IN',('purchase',)),),
            description='Сумма списанного престижа по дате завершения покупки. Бесплатные выдачи не увеличивают расход.',
            guide='**Расход за день**, а не остаток кошелька. Учитываются только покупки за престиж. Другие списания кошелька и его пополнения здесь не показаны.'),),
        (history('purchases-history','История покупок и бесплатных выдач',
            ('occurred_at','offer_name','currency_name','charged_amount','reference_id'),'purchase',
            '**Каждая строка — завершённая операция магазина.** Цена 0 означает бесплатную выдачу; ID покупки позволяет найти её в сервисе магазина. Показаны до 1000 последних операций выбранного периода.'),),
        (history('runs-history','История завершённых забегов',
            ('occurred_at','outcome_name','church_level','hero_kills','tower_kills','building_upgrades','run_xp'),'run',
            '**Результат каждого забега.** Уровень церкви зафиксирован при завершении. Пустой опыт означает отсутствие сведений о награде; известный 0 сохранён. Показаны до 1000 последних результатов.'),),
        (history('timeline','История действий игрока',('occurred_at','activity_label','detail'),description=
            '**Единая история шести типов записей.** Покупки, забеги, начисления опыта, повышения, подарки и кейсы. Связанные записи могут относиться к одному действию. Самые новые — сверху; до 1000 строк за период.'),),
    )
    prefix='ДЕМО · ' if demo else 'VaticanDefendor · '
    note='### Статистика выбранного игрока\n\nИтоги, покупки, действия в забегах и история подтверждённых операций.'
    if demo:
        note+=f' Синтетические данные · срез {reference_at:%d.%m.%Y %H:%M} UTC.'
    return Board('vatican-demo-player' if demo else 'vatican-player',prefix+'Игрок',note,rows,
        'player_id',dataset,intro_sections=(
            ('Выбор игрока','- Выберите одного игрока по **ID**.\n- Найдите его поиском или вставьте ID целиком.\n- Все показатели используют один выбор.'),
            ('Период и расчёт','- Даты действий и завершения операций, время UTC.\n- Опыт считается по начислениям, без повторного прибавления из забегов.\n- Уровни — прибавка за период.'),
            ('Как читать','- Только записи, поступившие в аналитику.\n- Один игрок на весь дашборд; выбор обязателен.\n- Полный ID — в сводной таблице.')),
        show_limit_note=False,footer_height=28,reading_after_row=1,
        footer='### Данные и ограничения\n\n- Витрины обновляются каждые 5 минут, кэш графиков — до 60 секунд. Свежесть проверяйте в «Обновлении BI».\n- При пустом выборе игрока общая статистика не подставляется. Для неизвестного ID или периода без записей истории пусты.\n- Карточки учитывают весь период; в истории — до 1000 последних строк. Для детализации сузьте период. Ошибка загрузки не означает ноль.',
        row_headers=((0,'Забеги и прогресс'),(1,'Покупки и награды'),(2,'Действия и активность'),
                     (5,'Истории операций')))
