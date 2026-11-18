from dishka.integrations.fastapi import FromDishka, inject_sync
from fastapi import APIRouter, Depends, Query
from pydantic import BaseModel, Field

from .routes import service_access
from .documentation import error_responses
from ..application.reports import ReadDailyReport


class RunTotals(BaseModel):
    completed: int = Field(description='Завершённые забеги за сутки, без повторных доставок.')
    players: int = Field(description='Разные игроки с забегом за эти сутки.')
    victories: int = Field(description='Число побед.')
    defeats: int = Field(description='Число поражений.')
    heroKills: int = Field(description='Сумма убийств героем в завершённых забегах.')
    towerKills: int = Field(description='Сумма убийств башнями в завершённых забегах.')
    buildingUpgrades: int = Field(description='Сумма улучшений построек.')
    knownXpAwarded: int = Field(description='Сумма только известных наград опыта. При пропусках это неполная сумма.')
    runsWithXp: int = Field(description='Забеги с известным начислением, включая известный ноль.')
    runsWithoutXp: int = Field(description='Забеги без сведений о награде; не считаются нулевыми начислениями.')


class LevelTotals(BaseModel):
    transitions: int = Field(description='Число записей о повышении уровня.')
    players: int = Field(description='Разные игроки с повышением за сутки.')
    levelsGained: int = Field(description='Сумма прибавок уровня. Переход 1 → 3 даёт +2 уровня, но одно событие.')


class GiftTotals(BaseModel):
    claims: int = Field(description='Число полученных подарков; ожидающие выдачи исключены.')
    players: int = Field(description='Разные получатели подарков за сутки.')


class CaseTotals(BaseModel):
    openings: int = Field(description='Полностью завершённые открытия кейсов.')
    players: int = Field(description='Разные игроки с открытием за сутки.')
    guaranteedOpenings: int = Field(description='Количество фактических срабатываний гаранта, не вероятность.')
    paidFromInventory: int = Field(description='Открытия с оплатой кейсом из инвентаря.')
    paidFromWallet: int = Field(description='Открытия с оплатой из кошелька; не сумма списания.')


class DailyReport(BaseModel):
    day: str = Field(description='Отчётная дата YYYY-MM-DD.')
    timezone: str = Field(description='Границы суток определены в UTC.')
    runs: RunTotals = Field(description='Подтверждённые результаты забегов.')
    levelUps: LevelTotals = Field(description='Повышения и прибавка уровней.')
    levelGifts: GiftTotals = Field(description='Фактическое получение подарков.')
    cases: CaseTotals = Field(description='Завершённые кейсы и работа гаранта.')


router = APIRouter(prefix='/internal/reports', dependencies=[Depends(service_access)])

DAILY_EXAMPLE={'day':'2026-11-17','timezone':'UTC',
    'runs':{'completed':3,'players':2,'victories':2,'defeats':1,'heroKills':12,'towerKills':15,
            'buildingUpgrades':9,'knownXpAwarded':80,'runsWithXp':2,'runsWithoutXp':1},
    'levelUps':{'transitions':1,'players':1,'levelsGained':2},'levelGifts':{'claims':1,'players':1},
    'cases':{'openings':2,'players':2,'guaranteedOpenings':1,'paidFromInventory':1,'paidFromWallet':1}}
DAILY_RESPONSES=error_responses((401,422,500,503))
EMPTY_EXAMPLE={'day':'2026-11-18','timezone':'UTC',
    **{name:{key:0 for key in DAILY_EXAMPLE[name]} for name in ('runs','levelUps','levelGifts','cases')}}
DAILY_RESPONSES[200]={'description':'Итоги суток UTC. Пропуски опыта показаны отдельно.',
    'content':{'application/json':{'examples':{
        'activity':{'summary':'Сутки с активностью и пропуском опыта','value':DAILY_EXAMPLE},
        'empty':{'summary':'За сутки нет записанных фактов','value':EMPTY_EXAMPLE}}}}}
DAILY_RESPONSES[503]['content']['application/json']['examples']['warehouse']={
    'summary':'Хранилище недоступно; нулевой отчёт не подставляется',
    'value':{'code':'ServiceUnavailable','detail':'Analytics report is temporarily unavailable',
             'requestId':'33333333-3333-4333-8333-333333333333','errors':[]}}


@router.get('/daily', response_model=DailyReport, tags=['Отчёты'], summary='Получить дневные итоги в UTC', responses=DAILY_RESPONSES)
@inject_sync
def daily(use_case: FromDishka[ReadDailyReport], day: str = Query(min_length=10, max_length=10, description="UTC-день в формате YYYY-MM-DD, годы 1970–2200.", examples=["2026-11-17"])):
    """Итоги по фактам за сутки UTC: от 00:00 включительно до следующего 00:00 исключительно.

    Повторные доставки не увеличивают итог. Поздняя доставка может изменить прошлый день.
    knownXpAwarded — сумма только известных наград; runsWithoutXp показывает пропуски,
    которые нельзя считать нулевым опытом. Игроки уникальны внутри каждого раздела:
    аудитории разделов не складываются в общую аудиторию.

    В этом отчёте есть забеги, повышения уровня, подарки и кейсы. Покупки и движения
    кошелька принимаются отдельными событиями, но в этот метод не включены.
    При сбое ClickHouse возвращается 503; нулевой отчёт вместо ошибки не формируется.
    """
    return use_case.execute(day)
