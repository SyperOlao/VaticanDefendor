from copy import deepcopy
from . import response_schemas as s
from ..migrate import SCHEMA_REVISION

ACCOUNT = '8be01f80-0f28-4b43-b718-05c4cd96d9a2'
OPERATION = 'f567fc37-f14f-40a3-b8f4-24b5bac603e3'
REQUEST = 'a8c9596f-6ac4-4214-97b4-c2f432065aad'
DESCRIPTION = """Валюты, балансы и история транзакций VaticanDefendor.

Для клиентских запросов получите токен в auth и вставьте его в **Authorize → BearerAuth** без `Bearer`.
Аккаунт берётся только из токена. `GET /v1/wallet` при первом обращении открывает PRESTIGE-баланс с начальным начислением; история сама баланс не открывает.

Магазин и game отправляют финансовые команды через **RabbitMQ**, результат читают через **gRPC**.
Внутренние HTTP-команды сохранены для явно выбранного режима совместимости. Для них нужен **ServiceSecret** — значение `VD_SERVICE_SECRET`.
Пользовательский токен не заменяет этот секрет; секрет не передают игровому клиенту.

Списание и возврат имеют один `operationId` покупки, но разные идемпотентные операции. При таймауте повторяйте прежние идентификаторы и сумму.
HTTP 200 может содержать `status: REJECTED`: это сохранённый бизнес-отказ, а не успешная покупка.
`/internal/messages` обрабатывает команду синхронно; `APPLIED` доставки может содержать отказ в `receipt`.
В broker status `PENDING` ещё не подтверждает эффект; `REVIEW` требует разбора и явного replay.

Все UUID в примерах вымышленные. Ошибка содержит `code`, `detail`, `requestId`, `errors`; ID совпадает с `X-Request-ID`.
"""
TAGS = [{'name':'Кошелёк'}, {'name':'Внутренние команды'}, {'name':'Восстановление доставки'}, {'name':'Доступность'}]
OPERATIONS = {
 'live': ('Проверить HTTP-процесс', 'Доступность', 'Без авторизации. Не проверяет базу.', s.Liveness, ()),
 'ready': ('Проверить готовность схемы', 'Доступность', 'Без авторизации. Проверяет собственную БД wallet и ревизию миграций; соседние сервисы не проверяет.', s.Readiness, ()),
 'balance': ('Получить балансы текущего игрока', 'Кошелёк', 'Нужен Bearer-токен auth. Верхние поля относятся к PRESTIGE, balances содержит все валюты аккаунта.', s.WalletBalance, (401,)),
 'history': ('Посмотреть историю изменения денег', 'Кошелёк', 'Нужен Bearer-токен. Страницы идут от новых записей к старым. Передавайте nextCursor как before; чужой/неподходящий курсор даёт 422.', s.History, (401,422)),
 'charge': ('Списать престиж за покупку', 'Внутренние команды', 'Нужен X-Service-Secret. Режим HTTP совместимости. Повтор accountId/operationId/amount возвращает сохранённый результат; другая сумма с тем же ключом даёт 409. InsufficientFunds возвращается с HTTP 200 и status=REJECTED.', s.AppliedChange | s.RejectedChange, (401,409,422)),
 'refund': ('Вернуть списанный престиж', 'Внутренние команды', 'Нужен X-Service-Secret. Используйте UUID и сумму исходной покупки. Возврат однократный; без соответствующего списания возвращается HTTP 200, REJECTED / ChargeNotFound.', s.AppliedChange | s.RejectedChange, (401,409,422)),
 'receive': ('Обработать серверную финансовую команду', 'Внутренние команды', 'Нужен X-Service-Secret. Синхронный HTTP consumer той же бизнес-логики, что RabbitMQ. schemaVersion=1. Сохраните messageId и исходный payload при повторе. Доступные типы указаны в eventName.', s.AppliedMessage, (401,409,422)),
 'command_status': ('Прочитать сохранённый результат команды', 'Восстановление доставки', 'Нужен X-Service-Secret. Для диагностики: сообщение, состояние, hash и receipt. Для штатной связи сервисов используется gRPC GetCommandStatus. Неизвестный UUID даёт 404.', s.CommandStatus, (401,404,422)),
 'replay_command': ('Повторить команду из REVIEW или REJECTED', 'Восстановление доставки', 'Нужен X-Service-Secret. Только после разбора причины. Сохраняет источник и увеличивает поколение обработки; сам финансовый эффект защищён бизнес-идемпотентностью. Нельзя этим отменить сохранённый бизнес-отказ по покупке.', s.Changed, (401,422)),
 'replay_event': ('Повторить исчерпанную dead-letter публикацию', 'Восстановление доставки', 'Нужен X-Service-Secret. Используйте eventId из статуса команды. Не повторяет само списание или начисление.', s.Changed, (401,422)),
}

def ex(summary, value):return {'summary':summary,'value':value}
BALANCE = {'accountId':ACCOUNT,'currencyId':'PRESTIGE','balance':500,'revision':1,
           'balances':[{'currencyId':'PRESTIGE','balance':500,'revision':1}]}
CHANGE = {'accountId':ACCOUNT,'operationId':OPERATION,'currencyId':'PRESTIGE','amount':25}
REWARD = {'messageId':OPERATION,'eventName':'run.currency_reward','schemaVersion':1,
          'payload':{'accountId':ACCOUNT,'runId':REQUEST,'amount':25}}
SUCCESS = {
 'live': {'live':ex('Процесс работает',{'status':'live'})},
 'ready': {'ready':ex('Схема готова',{'status':'ready','schemaRevision':SCHEMA_REVISION})},
 'balance': {'balance':ex('Начальное состояние',BALANCE)},
 'history': {'empty':ex('Пустая история',{'transactions':[],'nextCursor':None})},
 'charge': {'applied':ex('Деньги списаны',{**CHANGE,'status':'APPLIED','balance':475}),
            'rejected':ex('Недостаточно денег',{**CHANGE,'status':'REJECTED','code':'InsufficientFunds'})},
 'refund': {'applied':ex('Деньги возвращены',{**CHANGE,'status':'APPLIED','balance':500}),
            'rejected':ex('Нет исходного списания',{**CHANGE,'status':'REJECTED','code':'ChargeNotFound'})},
 'receive': {'applied':ex('Награда сохранена',{'messageId':OPERATION,'status':'APPLIED','receipt':{
     **REWARD['payload'],'currencyId':'PRESTIGE','balanceAfter':525}})},
 'command_status': {'pending':ex('Команда ожидает обработки',{'messageId':OPERATION,'payloadSha256':'a'*64,
     'status':'PENDING','attempts':0,'deliveryGeneration':1,'lastError':None,'result':None,'events':[]})},
 'replay_command': {'changed':ex('Повтор поставлен в обработку',{'changed':True})},
 'replay_event': {'changed':ex('Публикация поставлена в обработку',{'changed':True})},
}


def operation(name):
    summary, tag, description, model, statuses = OPERATIONS[name]
    responses = error_responses((*statuses,500,503))
    responses[200]={'model':model,'description':'См. status в теле ответа.', 'content':{'application/json':{'examples':SUCCESS[name]}}}
    return {'summary':summary,'description':description,'tags':[tag],'responses':responses}


def error_responses(statuses):
    values={401:('InvalidCredentials','Credential is invalid or expired'),404:('ResourceNotFound','Resource was not found'),
            409:('ResourceConflict','Operation conflicts with the current state'),422:('InvalidRequest','Request validation failed'),
            500:('InternalError','An internal error occurred'),503:('DatabaseUnavailable','Wallet database is temporarily unavailable')}
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
