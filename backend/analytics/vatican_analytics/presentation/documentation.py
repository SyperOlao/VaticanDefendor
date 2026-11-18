from copy import deepcopy
from uuid import UUID, uuid5
from . import schemas as s
from ..domain.events import EVENT, GIFT_EVENT, CASE_EVENT, PURCHASE_EVENT, WALLET_EVENT, EVENT_FIELDS, receipt
from ..migrate import SCHEMA_REVISION

ACCOUNT='8be01f80-0f28-4b43-b718-05c4cd96d9a2'
SOURCE='f567fc37-f14f-40a3-b8f4-24b5bac603e3'
REQUEST='a8c9596f-6ac4-4214-97b4-c2f432065aad'
AT='2030-01-01T12:00:00+00:00'
DESCRIPTION="""Сервис принимает серверные снимки забегов, подарков, кейсов, покупок и транзакций кошелька.
Сохраняет их в PostgreSQL inbox, затем отдельный worker строит проекции в ClickHouse.

Здесь нет пользовательского входа. В **Authorize → ServiceSecret** укажите `VD_SERVICE_SECRET`.
Секрет предназначен только для доверенных сервисов; accessToken игрока его не заменяет.

1. Передайте POST `/internal/messages` с постоянным messageId и примером нужного eventName.
2. **202 / PENDING** означает сохранение inbox, но ещё не завершение проекции.
3. Worker проецирует событие. Повтор исходного POST вернёт **200 / APPLIED** и receipt.
4. **409 / REVIEW** — исчерпаны попытки проекции; после исправления причины оператор повторяет источник через CLI `vatican_analytics.cli.replay_events`.
При 409 также возможна обычная ошибка конфликта с полями code/detail: смотрите схему и примеры ответа.

GET источника использует статусы PENDING/COMPLETED/REVIEW; COMPLETED соответствует APPLIED в POST.
Сверяйте receipt: ID источника, accountId, payloadHash и eventIds. Потерянный ответ повторяется с теми же данными.
Неизменяемый UUID источника берётся из payload, а messageId обозначает доставку — это разные идентификаторы.

Дневной отчёт возвращает eventual totals проекции за UTC-день; поздняя доставка может изменить прошлый день.
ClickHouse не нужен для приёма в inbox. `/health/ready` проверяет только PostgreSQL и ревизию inbox, не готовность проекции.
Все UUID, даты и снимки в примерах вымышленные. Ошибки: code/detail/requestId/errors, заголовок X-Request-ID.
"""
TAGS=[{'name':'Приём событий'},{'name':'Состояние проекции'},{'name':'Отчёты'},{'name':'Доступность'}]


def ex(summary,value):return {'summary':summary,'value':value}

PAYLOADS={
 EVENT:{'runId':SOURCE,'accountId':ACCOUNT,'rulesetId':'xp-v1','contentReleaseId':'release-v1',
     'completedAt':AT,'outcome':'victory','churchLevel':1,
     'stats':{'heroKills':{},'towerKills':{},'buildingUpgrades':{}},'progression':None},
 GIFT_EVENT:{'claimId':SOURCE,'accountId':ACCOUNT,'rulesetId':'xp-v1','rewardedLevel':2,
     'rewardBundleId':REQUEST,'status':'CLAIMED','claimedAt':AT},
 CASE_EVENT:{'openingId':SOURCE,'accountId':ACCOUNT,'rulesetId':'case-v1','caseId':REQUEST,'caseCode':'common',
     'paymentMode':'INVENTORY','rewardBundleId':REQUEST,'rarity':'RARE','pityBefore':2,'pityAfter':0,
     'guaranteedTriggered':True,'entryIndex':1,'status':'COMPLETED','completedAt':AT},
 PURCHASE_EVENT:{'purchaseId':str(uuid5(UUID(ACCOUNT),'shop.purchase:'+SOURCE)),'accountId':ACCOUNT,'operationId':SOURCE,
     'offerId':'sword','catalogVersion':'v1','currencyId':'PRESTIGE','chargedAmount':25,
     'weaponConfigurationId':'sword.rare','weaponInstanceIds':[REQUEST],'status':'Completed','completedAt':AT},
 WALLET_EVENT:{'transactionId':SOURCE,'accountId':ACCOUNT,'currencyId':'PRESTIGE','delta':25,'balanceAfter':525,
     'reason':'RUN_REWARD','sourceType':'RUN_REWARD','sourceId':REQUEST,'itemIndex':0,'occurredAt':AT},
}
REQUEST_EXAMPLES={event:ex(event,{'messageId':REQUEST if event==EVENT else str(uuid5(UUID(REQUEST),event)),
    'eventName':event,'schemaVersion':1,'payload':payload}) for event,payload in PAYLOADS.items()}
REQUEST_EXAMPLES[EVENT]['summary']='Завершённый забег без сведений об опыте'
for name,summary in ((GIFT_EVENT,'Полученный подарок за уровень'),(CASE_EVENT,'Завершённое открытие кейса'),
                     (PURCHASE_EVENT,'Подтверждённая покупка оружия'),(WALLET_EVENT,'Запись журнала кошелька')):
    REQUEST_EXAMPLES[name]['summary']=summary
REQUEST_EXAMPLES['run.analytics.with-xp']=ex('Забег с начислением опыта и повышением уровня',{
    'messageId':str(uuid5(UUID(REQUEST),'with-xp')),'eventName':EVENT,'schemaVersion':1,
    'payload':{**deepcopy(PAYLOADS[EVENT]),'runId':str(uuid5(UUID(SOURCE),'with-xp')),
        'stats':{'heroKills':{'goblin':4},'towerKills':{'goblin':6},'buildingUpgrades':{'tower.archer':2}},
        'progression':{'xpGranted':80,'totalXp':180,'previousLevel':1,'currentLevel':2,
            'breakdown':{'heroKillXp':20,'towerKillXp':30,'tycoonXp':10,'victoryXp':20}}}})
RECEIPT=receipt(PAYLOADS[EVENT],EVENT)
PENDING={'messageId':REQUEST,'status':'PENDING','receipt':None}
APPLIED={'messageId':REQUEST,'status':'APPLIED','receipt':RECEIPT}
REVIEW={'messageId':REQUEST,'status':'REVIEW','receipt':None}
SOURCE_PENDING={'sourceId':SOURCE,'eventName':EVENT,'runId':SOURCE,'accountId':ACCOUNT,'status':'PENDING',
                'attempts':0,'lastError':None,'receipt':None,'completedAt':None}
OPERATIONS={
 'live':('Проверить HTTP-процесс','Доступность','Без авторизации. Не проверяет PostgreSQL или ClickHouse.',s.Liveness,()),
 'ready':('Проверить готовность inbox','Доступность','Без авторизации. Проверяет PostgreSQL и схему inbox; не выполняет запросы ClickHouse.',s.Readiness,()),
 'receive':('Сохранить серверный снимок для аналитики','Приём событий','Нужен X-Service-Secret. Выберите пример одного из пяти eventName. Только schemaVersion=1. Снимок должен быть рассчитан владельцем события; клиент не отправляет произвольную статистику. Повторяйте исходный messageId и payload. 202 не подтверждает проекцию.',s.MessageResult,(401,409,422)),
 'status':('Прочитать состояние аналитики забега','Состояние проекции','Нужен X-Service-Secret. run_id — UUID забега из payload, а не messageId доставки. Неизвестный источник даёт 404.',s.RunSource,(401,404,422)),
 'source_status':('Прочитать состояние бизнес-источника','Состояние проекции','Нужен X-Service-Secret. kind: run, level-gift, case, shop-purchase или wallet-transaction. source_id — соответствующий runId/claimId/openingId/purchaseId/transactionId, не UUID доставки.',s.SourceStatus,(401,404,422)),
}
SUCCESS={
 'live':{'live':ex('Процесс работает',{'status':'live'})},
 'ready':{'ready':ex('Inbox готов',{'status':'ready','schemaRevision':SCHEMA_REVISION})},
 'receive':{'applied':ex('Проекция завершена',APPLIED)},
 'status':{'pending':ex('Ожидает проекции',SOURCE_PENDING)},
 'source_status':{'pending':ex('Забег ожидает проекции',SOURCE_PENDING)},
}


def error_responses(statuses):
    values={401:('InvalidCredentials','Credential is invalid or expired'),404:('ResourceNotFound','Resource was not found'),
            409:('ResourceConflict','Operation conflicts with the current state'),422:('InvalidRequest','Request validation failed'),
            500:('InternalError','An internal error occurred'),503:('DatabaseUnavailable','Analytics database is temporarily unavailable')}
    responses={}
    for status in statuses:
        code,detail=values[status]
        responses[status]={'model':s.ErrorResponse,'description':detail,'content':{'application/json':{'examples':{
            'error':ex(code,{'code':code,'detail':detail,'requestId':REQUEST,'errors':[]})}}},
            'headers':{'X-Request-ID':{'schema':{'type':'string','format':'uuid'},'description':'UUID запроса.'}}}
        if status==503:responses[status]['headers']['Retry-After']={'schema':{'type':'string'},'description':'Пауза перед повтором в секундах.','example':'1'}
    return responses


def preserve_examples(app):
    original=app.openapi
    def openapi():
        spec=original()
        from fastapi.routing import APIRoute
        for route in app.routes:
            if not isinstance(route,APIRoute) or not route.include_in_schema:continue
            for method in route.methods:
                operation=spec['paths'][route.path_format][method.lower()]
                for status,response in route.responses.items():
                    for media,content in response.get('content',{}).items():
                        if 'examples' in content:operation['responses'][str(status)]['content'][media]['examples']=deepcopy(content['examples'])
        return spec
    app.openapi=openapi



def operation(name):
    summary,tag,description,model,statuses=OPERATIONS[name]
    responses=error_responses((*statuses,500,503))
    responses[200]={'model':model,'description':'См. status в теле ответа.','content':{'application/json':{'examples':SUCCESS[name]}}}
    if name=='receive':
        responses[202]={'model':s.MessageResult,'description':'Снимок сохранён; проекция ещё не завершена.',
            'content':{'application/json':{'examples':{'pending':ex('Принято в inbox',PENDING)}}}}
        responses[409]['model']=s.MessageResult | s.ErrorResponse
        responses[409]['description']='REVIEW проекции либо ошибка конфликта неизменяемого источника/доставки.'
        responses[409]['content']['application/json']['examples']['review']=ex('Нужен разбор оператором',REVIEW)
    return {'summary':summary,'description':description,'tags':[tag],'responses':responses}


def install_openapi(app):
    from pydantic import TypeAdapter
    from .documentation_models import AnalyticsMessage
    preserve_examples(app)
    original=app.openapi
    def openapi():
        document=original()
        schema=TypeAdapter(AnalyticsMessage).json_schema(ref_template='#/components/schemas/{model}')
        document['components']['schemas'].update(schema.pop('$defs',{}))
        media=document['paths']['/internal/messages']['post']['requestBody']['content']['application/json']
        media['schema']=schema
        media['examples']=deepcopy(REQUEST_EXAMPLES)
        document['components']['schemas'].pop('Message',None)
        for param in document['paths']['/internal/sources/{kind}/{source_id}']['get'].get('parameters',[]):
            if param['name']=='kind':param['schema']['enum']=['run','level-gift','case','shop-purchase','wallet-transaction']
        return document
    app.openapi=openapi
