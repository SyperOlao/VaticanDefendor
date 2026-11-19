"""Human-facing API guide and route metadata. No business behavior is changed here."""
from copy import deepcopy
from fastapi.routing import APIRoute
from ..migrate import SCHEMA_REVISION
from . import response_schemas as r,openapi_examples as ex

DESCRIPTION='''
Игровой профиль, энергия, забеги, подарки, кейсы и постоянные улучшения VaticanDefendor.

### Как попробовать API
1. Получите `accessToken` после входа/регистрации либо гостевой `token` из `/v1/guests` в сервисе **auth**. В локальном Compose его Swagger находится по адресу
   [http://127.0.0.1:28081/docs](http://127.0.0.1:28081/docs). Авторизация и выдача токенов не входят в этот API.
2. Нажмите **Authorize → HTTPBearer** и вставьте только токен, без префикса `Bearer`.
3. Начните с **GET /v1/profile**, **GET /v1/energy** и **GET /v1/progression**.
4. Для LOCAL выполните `start → сохраните runId/sessionId и контекст → finish → опрашивайте состояние`.
   Для dedicated: `POST /v1/runs → GET /v1/runs/{runId} → подключение к выданному серверу`.
5. Для покупки престижа: `каталог → purchases → GET операции до COMPLETED или REJECTED`.

Примеры содержат **демонстрационные UUID и версии**. Перед выполнением подставьте значения из своего
каталога/профиля/ответа запуска. Контент, правила XP, энергия, LOCAL-policy, кейсы и улучшения должны быть
опубликованы оператором; сам запрос не создаёт эти конфигурации. У каждого метода есть примеры тела и ответа.

### Повторы и статусы
- Для методов с полем `requestKey` создайте UUID один раз на намерение, сохраните его и при таймауте повторите **тот же запрос**.
  Новый UUID означает новое намерение, а изменение тела старой операции приводит к 409.
- **HTTP 202**: операция сохранена и ещё выполняется; продолжайте GET/повтор исходного запроса.
- **HTTP 200**: запрос обработан. Проверяйте `status` внутри ответа: это может быть сохранённый отказ.
  В частности, LOCAL `finish` возвращает 200 и при `validationStatus=REJECTED`.
- `PENDING` — ждём обработки; `REVIEW` — нужно исправление причины оператором и повтор сохранённого
  сообщения. Не создавайте новую покупку для обхода неизвестного исхода.
- `rewardStatus`, `ratingStatus`, `analyticsStatus` **независимы**. Награды могут быть выданы, пока аналитика
  ещё ожидает доставки. `SKIPPED` означает отсутствие сообщения (например, историческая операция).
- Время с суффиксом `At` и дедлайны — UTC ISO 8601. Длительности `*Ms` — миллисекунды,
  `grantExpiresAt` — Unix timestamp в секундах.

### Доступ и границы
`/v1` с замком использует аккаунт из accessToken. Публичные каталоги доступны без токена.
`/internal/servers/*` предназначен только для Unity dedicated server: в Authorize выберите
**DedicatedServerSecret** и укажите секрет соответствующего serverId. Публикация контента использует
отдельный **ContentPublisherSecret**. Секреты этих разделов не передаются игровому клиенту.

Wallet, inventory, shop, rating, auth и analytics имеют собственные API/Swagger.
Через общий gateway `/docs` показывает именно документацию game_backend.
'''

TAGS=[
    {'name':'01 · Профиль','description':'Ник, аватар и настройки. Для изменения нужна актуальная version из GET.'},
    {'name':'02 · Энергия и уровни','description':'Текущая энергия, стоимость старта и серверная прогрессия XP.'},
    {'name':'03 · LOCAL-забеги','description':'Запуск и сдача локальной сводки. Проверка правдоподобности не доказывает честность локальной симуляции.'},
    {'name':'04 · Dedicated-забеги','description':'Назначение доверенного Unity-сервера и выдача краткоживущего допуска.'},
    {'name':'05 · Подарки за уровни','description':'Просмотр и однократное получение подарков, включая частичную выдачу.'},
    {'name':'06 · Кейсы','description':'Публичный каталог, расходование/оплата и один сохранённый розыгрыш.'},
    {'name':'07 · Постоянные улучшения','description':'Каталог престижа → покупка → состояние. Бонус действует со следующего забега.'},
    {'name':'08 · Контент и определения','description':'Публичные неизменяемые релизы, определения оружия и состав наборов наград.'},
    {'name':'90 · Dedicated server — внутреннее API','description':'Только серверные процессы. Требуется X-Server-Secret для данного serverId.'},
    {'name':'91 · Публикация контента — внутреннее API','description':'Операторская публикация с X-Content-Admin-Secret.'},
    {'name':'99 · Состояние сервиса','description':'Liveness и готовность игровой схемы. Готовность соседних сервисов проверяется отдельно.'},
]


def example(value,summary,description=None):
    result={'summary':summary,'value':deepcopy(value)}
    if description:result['description']=description
    return result


def response(model,description,examples):
    return {'model':model,'description':description,'content':{'application/json':{'examples':examples}},
        'headers':{'X-Request-ID':{'description':'ID запроса для диагностики.','schema':{'type':'string','format':'uuid'}}}}


def operation(tag,summary,description,model,examples,*,status=200,accepted=None,errors=(401,404,409,422,503)):
    result={'tags':[tag],'summary':summary,'description':description,
        'responses':{status:response(model,'См. status и отдельные состояния доставки в теле ответа.',examples)},'errors':errors}
    if accepted:result['responses'][202]=response(model,'Намерение сохранено; продолжайте опрашивать состояние.',accepted)
    return result


PENDING_RUN={'runId':ex.RUN,'status':'pending','assignmentExpiresAt':'2026-11-17T12:00:30+00:00'}
GRANT={'runId':ex.RUN,'serverId':'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb','host':'127.0.0.1','port':7979,
    'transport':'udp-loopback','certificateName':'','contentReleaseId':ex.RELEASE,'protocolVersion':3,
    'profileVersion':1,'profileDisplayName':'Рыцарь','grant':'DEMO_CONNECTION_GRANT_NOT_VALID','grantExpiresAt':1794916830}
PRESTIGE_LEVEL={'level':1,'cost':100,'modifierValue':'0.050000'}
PRESTIGE_CATALOG={'rulesetId':'prestige-v1','available':True,'upgrades':[{'code':'hero.attack_damage','target':'HERO',
    'stat':'ATTACK_DAMAGE','levels':[PRESTIGE_LEVEL],'currentLevel':0,'maxLevel':1,'requiresReview':False,'nextLevel':PRESTIGE_LEVEL}],
    'bonuses':ex.BONUSES,'legacyUpgrades':[],'requiresReview':False,'pendingPurchase':None}
GIFT_PENDING={**ex.GIFT,'status':'PENDING','deliveryStatus':'PENDING','requestedAt':ex.AT,
    'lines':[{**ex.BUNDLE_RESPONSE['items'][0],'status':'PENDING','messageId':ex.MESSAGE,'receipt':None,'completedAt':None}]}
SERVER_ASSIGNED={'assigned':True,'runId':ex.RUN,'accountId':ex.ACCOUNT,'contentReleaseId':ex.RELEASE,
    'rulesetId':'xp-local-v1','profileVersion':1,'displayName':'Рыцарь','generation':1,
    'weaponsJson':'{"Configurations":[],"Weapons":[],"Loadout":[],"Revision":1}',
    'prestigeJson':'{"schemaVersion":1,"upgrades":[],"modifiers":[]}','assignmentExpiresAt':'2026-11-17T12:00:30+00:00'}

OPERATIONS={
    ('GET','/v1/profile'):operation(TAGS[0]['name'],'Мой профиль','Версия и настройки аккаунта из токена. Деньги читаются в wallet, оружие — в inventory.',r.ProfileResponse,{'profile':example(ex.PROFILE,'Профиль игрока')},errors=(401,404,503)),
    ('GET','/v1/profile/display-names'):operation(TAGS[0]['name'],'Ники участников рейтинга','Возвращает актуальные ники для известных UUID. Неизвестные UUID пропускаются.',r.PlayerDisplayNames,{'players':example({'players':[{'accountId':ex.ACCOUNT,'displayName':'Рыцарь'}]},'Ники игроков')},errors=(401,422,503)),
    ('PUT','/v1/profile'):operation(TAGS[0]['name'],'Изменить ник и настройки (совместимый PUT)','Ник обязателен. Пропущенные настройки сохраняются. Занятый ник с любым регистром даёт 409 DisplayNameTaken. При конфликте версии перечитайте профиль.',r.ProfileResponse,{'updated':example({**ex.PROFILE,'version':2},'Сохранено с новой версией')}),
    ('PATCH','/v1/profile'):operation(TAGS[0]['name'],'Изменить отдельные поля профиля','Передайте expectedVersion и хотя бы одно поле. avatarAssetKey=null сбрасывает аватар. Для ника, языка и часового пояса null запрещён. Занятый ник с любым регистром даёт 409 DisplayNameTaken.',r.ProfileResponse,{'updated':example({**ex.PROFILE,'version':2},'Настройки сохранены'),'cleared':example({**ex.PROFILE,'version':2,'avatarAssetKey':None},'Аватар сброшен')}),
    ('GET','/v1/profile/avatars'):operation(TAGS[0]['name'],'Доступные аватары','Скопируйте assetKey выбранного аватара в PUT/PATCH профиля. Загрузка пользовательских фотографий этим API не поддерживается.',r.AvatarCatalog,{'avatars':example({'avatars':[{'assetKey':'avatars/knight','title':'Рыцарь','symbol':'Р','backgroundColor':'#79513E'}]},'Каталожный аватар')},errors=(401,503)),
    ('GET','/v1/energy'):operation(TAGS[1]['name'],'Моя энергия и цена забега','Восстановление рассчитывается по серверному UTC при чтении. Стоимость старта выбирает сервер. Купленная энергия сверх maxEnergy сохраняется.',r.EnergyResponse,{'energy':example(ex.ENERGY,'После списания 5 единиц')},errors=(401,404,503)),
    ('GET','/v1/progression'):operation(TAGS[1]['name'],'Мой XP, уровень и доступные подарки','availableRewards — открытые подарки, которые ещё нужно получить через claim. Это не выданное содержимое подарков.',r.ProgressionResponse,{'progress':example({'accountId':ex.ACCOUNT,'totalXp':70,'currentLevel':2,'rulesetId':'xp-local-v1','nextLevelTotalXp':100,'availableRewards':ex.XP['unlockedRewards']},'Второй уровень с доступным подарком')},errors=(401,404,503)),
    ('POST','/v1/local-runs'):operation(TAGS[2]['name'],'Запустить LOCAL-забег','Укажите опубликованную policyId. Контекст, loadout и бонусы закрепляются, энергия списывается один раз. Сохраните runId/sessionId и весь контекст для finish. Повтор requestKey возвращает прежний забег; одна активная сессия на аккаунт, включая dedicated.',r.LocalRunResponse,{'started':example(ex.LOCAL,'Запуск сохранён; можно начинать симуляцию')}),
    ('GET','/v1/local-runs/active'):operation(TAGS[2]['name'],'Восстановить активный LOCAL-забег','После перезапуска клиента сначала проверьте этот метод. Он не создаёт новый забег и не списывает энергию. Просроченный несданный забег закрывается.',r.ActiveLocalResponse,{'none':example({'active':False,'run':None},'Активного LOCAL нет'),'active':example({'active':True,'run':ex.LOCAL},'Продолжить сохранённую сессию')},errors=(401,404,503)),
    ('GET','/v1/local-runs/{run_id}'):operation(TAGS[2]['name'],'Состояние LOCAL и доставки наград','Владелец определяется токеном. ACCEPTED/PENDING означает, что сводка сохранена, но начисление ещё восстанавливается. settlementStatus=COMPLETED и reward/rating/analyticsStatus описывают разные этапы.',r.LocalRunResponse,{'running':example(ex.LOCAL,'Ещё не сдан'),'accepted':example(ex.LOCAL_ACCEPTED,'Начислено; внешняя доставка ожидается')},errors=(401,404,503)),
    ('POST','/v1/local-runs/{run_id}/finish'):operation(TAGS[2]['name'],'Сдать результат LOCAL-забега','Скопируйте идентификаторы из start. Счётчики и длительность проверяются по закреплённой policy. HTTP 200 с validationStatus=REJECTED — отказ без наград. Первая корректная по формату сводка неизменяема: точный повтор безопасен, другая сводка того же runId даёт 409. После ошибки начисления повтор/worker продолжит ACCEPTED даже после дедлайна.',r.LocalRunResponse,{'accepted':example(ex.LOCAL_ACCEPTED,'Принято и начислено'),'rejected':example(ex.LOCAL_REJECTED,'Отклонено без наград','Исправить уже сохранённую сводку этим же runId нельзя; начните новое намерение только для нового забега.')}),
    ('POST','/v1/local-runs/{run_id}/abandon'):operation(TAGS[2]['name'],'Завершить LOCAL без наград','Закрывает несданный забег. Уже потраченная при старте энергия не возвращается. Принятый результат нельзя отменять вместо восстановления начисления.',r.LocalRunResponse,{'abandoned':example({**ex.LOCAL,'status':'failed'},'Забег закрыт')}),
    ('POST','/v1/runs'):operation(TAGS[3]['name'],'Запросить dedicated-сервер','Сохраните requestKey и повторяйте его при таймауте. HTTP 202 возвращается также при повторе старого намерения. Опрашивайте GET забега; готовый ответ содержит host/port/grant. Отдельный сервер подтверждает назначение ACK. Без свободного совместимого процесса — 503.',r.RunStatusResponse,{'pending':example(PENDING_RUN,'Назначение ожидает ACK')},status=202),
    ('GET','/v1/runs/{run_id}'):operation(TAGS[3]['name'],'Состояние забега или параметры подключения','Для pending продолжайте опрос; готовый dedicated возвращает поля подключения без status. Для completed читаются сохранённый итог и доставки. Если runId принадлежит LOCAL, возвращается LocalRunResponse. grant — допуск Unity, а не токен auth.',r.RunStatusResponse|r.LocalRunResponse,{'pending':example(PENDING_RUN,'Ещё ожидается ACK'),'ready':example(GRANT,'Можно подключаться'),'completed':example({**ex.LOCAL_ACCEPTED['settlement'],'status':'completed','rewardStatus':'PENDING','ratingStatus':'PENDING','rating':ex.LOCAL_ACCEPTED['rating'],'analyticsStatus':'PENDING','analytics':ex.PENDING},'Итог dedicated сохранён')}),
    ('POST','/v1/runs/{run_id}/grant'):operation(TAGS[3]['name'],'Получить свежий допуск к серверу','Для текущего готового dedicated-забега. Не создаёт нового забега и не списывает энергию. Для LOCAL допуски dedicated не выдаются.',r.RunStatusResponse,{'grant':example(GRANT,'Новый краткоживущий grant')}),
    ('GET','/v1/level-rewards'):operation(TAGS[4]['name'],'Мои подарки за уровни','Листайте limit/offset; claimId берите из этого ответа или availableRewards прогрессии. Каждый подарок принадлежит аккаунту из токена.',r.LevelRewardList,{'gifts':example({'claims':ex.XP['unlockedRewards'],'limit':50,'offset':0},'Открытые подарки')},errors=(401,404,422,503)),
    ('GET','/v1/level-rewards/{claim_id}'):operation(TAGS[4]['name'],'Состав и состояние подарка','lines показывают выдачу по строкам. PARTIAL означает, что часть уже выдана, другая ещё доставляется. Получение содержимого — отдельный POST claim.',r.LevelRewardResponse,{'available':example(ex.GIFT,'Можно получить'),'pending':example(GIFT_PENDING,'Выдача продолжается')},errors=(401,404,422,503)),
    ('POST','/v1/level-rewards/{claim_id}/claim'):operation(TAGS[4]['name'],'Получить подарок один раз','Тело {} или без тела. Деньги/предметы доставляются worker, энергия и EFFECT применяются локально. Повтор продолжает сохранённый план; уже выполненные строки и временные эффекты не применяются заново. REVIEW требует исправления и replay оператором.',r.LevelRewardResponse,{'claimed':example({**ex.GIFT,'status':'CLAIMED','deliveryStatus':'COMPLETED','requestedAt':ex.AT,'claimedAt':ex.AT,'lines':[{**ex.GIFT['lines'][0],'status':'COMPLETED','messageId':ex.MESSAGE,'receipt':{'currencyId':'PRESTIGE','quantity':10},'completedAt':ex.AT}],'analyticsStatus':'PENDING','analytics':ex.PENDING},'Выдано, аналитика ещё доставляется')},accepted={'pending':example(GIFT_PENDING,'Подарок сохранён, выдача ожидается')}),
    ('GET','/v1/cases'):operation(TAGS[5]['name'],'Каталог кейсов и цены','Публичный список опубликованных версий. Для открытия используйте caseId. WALLET оплачивает указанную цену; INVENTORY расходует принадлежащий игроку кейс.',r.CaseCatalog,{'catalog':example({'cases':[{'caseId':ex.CASE,'code':'starter-case','rulesetId':'cases-v1','priceCurrency':'PRESTIGE','price':100,'pityThreshold':10,'guaranteedRarity':'RARE'}],'limit':50,'offset':0},'Опубликованный кейс')},errors=(422,503)),
    ('POST','/v1/cases/{case_id}/open'):operation(TAGS[5]['name'],'Открыть кейс: оплатить и получить награду','Один requestKey на одно открытие. FUNDING ожидает оплаты/расходования, PENDING — выдачи наград. Розыгрыш и гарант сохраняются один раз. REJECTED — подтверждённый отказ оплаты без наград; REVIEW — неопределённый исход, не повод создавать новое открытие.',r.CaseOpeningResponse,{'rejected':example({**ex.OPEN_CASE,'status':'REJECTED','deliveryStatus':'REJECTED','completedAt':ex.AT,'paymentReceipt':{'accountId':ex.ACCOUNT,'openingId':ex.OPENING,'caseCode':'starter-case','currencyId':'PRESTIGE','amount':100,'outcome':'REJECTED','balanceAfter':50,'code':'InsufficientFunds'}},'Сохранённый отказ оплаты')},accepted={'funding':example(ex.OPEN_CASE,'Оплата поставлена в очередь')}),
    ('GET','/v1/case-openings/{opening_id}'):operation(TAGS[5]['name'],'Сохранённый розыгрыш и выдача кейса','Опрашивайте openingId из open, не выполняя новое открытие. Статусы наград и аналитики независимы.',r.CaseOpeningResponse,{'funding':example(ex.OPEN_CASE,'Ещё ждём оплату')},errors=(401,404,422,503)),
    ('GET','/v1/prestige-upgrades'):operation(TAGS[6]['name'],'Каталог постоянных улучшений и мои бонусы','Скопируйте rulesetId, code и currentLevel в запрос покупки. available=false означает отсутствие выбранной опубликованной конфигурации. pendingPurchase помогает восстановить неизвестный результат. ARCHER — стационарная башня; поддержанные характеристики указаны в levels/target/stat.',r.PrestigeCatalog,{'catalog':example(PRESTIGE_CATALOG,'Пример одного доступного улучшения')},errors=(401,404,503)),
    ('POST','/v1/prestige-upgrades/purchases'):operation(TAGS[6]['name'],'Купить следующий уровень за престиж','Цена/бонус выбираются сервером. expectedLevel — текущий уровень, а не желаемый. Один незавершённый purchase на аккаунт. COMPLETED означает оплату и применение; REJECTED — сохранённый отказ. При таймауте повторяйте тот же requestKey. Бонус попадёт в следующий старт, текущий забег сохраняет прежний снимок.',r.PrestigePurchaseResponse,{'completed':example(ex.PRESTIGE_COMPLETED,'Оплачено и применено'),'rejected':example(ex.PRESTIGE_REJECTED,'Недостаточно престижа','После пополнения старое намерение остаётся отказом; для новой попытки нужен новый requestKey.')},accepted={'pending':example(ex.PRESTIGE,'Покупка сохранена'),'review':example({**ex.PRESTIGE,'status':'REVIEW','attempts':20,'lastError':'DeliveryUnavailable'},'Требуется восстановление оператором')}),
    ('GET','/v1/prestige-upgrades/purchases/{purchase_id}'):operation(TAGS[6]['name'],'Состояние покупки улучшения','Опрашивайте до COMPLETED/REJECTED. PENDING/REVIEW после удалённого списания не означает потерю бонуса: worker получает прежний чек и продолжает применение без второго дебета.',r.PrestigePurchaseResponse,{'pending':example(ex.PRESTIGE,'Ещё выполняется'),'completed':example(ex.PRESTIGE_COMPLETED,'Купленный уровень и чек')},errors=(401,404,422,503)),
    ('GET','/v1/weapon-configurations'):operation(TAGS[7]['name'],'Публичные определения оружия','Это параметры типов оружия, а не принадлежащие игроку экземпляры. Собственное оружие/loadout читайте в inventory. В примере показаны основные поля; конфигурации содержат дополнительные параметры способностей.',list[r.WeaponConfigurationResponse],{'weapons':example([{'Id':'sword.common','Name':'Меч','Rarity':0,'Damage':30,'AttackInterval':0.25,'MaximumLevel':20,'ExperiencePerLevel':100,'IconUrl':'/v1/weapons/media/sword.common.png','ImageUrl':'/v1/weapons/media/sword.common.png'}],'Основные поля определения')},errors=(503,)),
    ('GET','/v1/content/{release_id}'):operation(TAGS[7]['name'],'Загрузить опубликованный ContentRelease','Публичная неизменяемая версия. payloadSha256 проверяется по исходным байтам payloadJson. Данные реального релиза экспортируются Unity, а не создаются по сокращённому примеру вручную.',r.ContentResponse,{'content':example(ex.PUBLICATION,'Сокращённая контентная оболочка')},errors=(404,422,503)),
    ('GET','/v1/reward-bundles/{bundle_id}'):operation(TAGS[7]['name'],'Посмотреть состав набора наград','Публичное чтение определения. Ничего не выдаёт игроку; для выдачи используется конкретный подарок или открытие кейса.',r.RewardBundleResponse,{'bundle':example(ex.BUNDLE_RESPONSE,'Престиж в наборе')},errors=(404,422,503)),
    ('PUT','/internal/content/{release_id}'):operation(TAGS[9]['name'],'Опубликовать неизменяемый ContentRelease','Только издатель контента с отдельным секретом. releaseId в URL/теле/_releaseId должен совпадать, payloadSha256 — совпадать с SHA-256 строки. Точный повтор безопасен, изменение существующего релиза даёт 409. Пример сокращён и не заменяет экспорт ContentReleasePublisher Unity.',r.Acknowledgement,{'published':example({'releaseId':ex.RELEASE,'replayed':False},'Версия опубликована')},errors=(401,409,422,503)),
    ('GET','/health/live'):operation(TAGS[10]['name'],'Процесс отвечает','Не проверяет БД, wallet или Unity-процессы.',r.HealthResponse,{'live':example({'status':'live'},'Процесс запущен')},errors=()),
    ('GET','/health/ready'):operation(TAGS[10]['name'],'Игровая схема готова','Проверяет актуальный head игровой PostgreSQL. Не заменяет healthchecks соседних сервисов.',r.HealthResponse,{'ready':example({'status':'ready','schemaVersion':SCHEMA_REVISION},'Миграции применены')},errors=(503,)),
}

SERVER_DESCRIPTIONS={
    'ready':('Зарегистрировать готовый процесс','Версии и транспорт должны совпадать с настроенным слотом. prestigeSchemaVersion=1 обязателен для назначения игрока с постоянными бонусами.'),
    'assignment':('Проверить назначение процесса','assigned=false — пока нет забега. При назначении сохраните runId/generation, загрузите weaponsJson и примените prestigeJson до ACK.'),
    'ack':('Подтвердить готовность к забегу','Отправляйте после загрузки контента, оружия и бонусов. Сверяются serverId, runId и generation. Повтор текущего ACK безопасен.'),
    'heartbeat':('Продлить присутствие процесса','Передавайте актуальное поколение. Истечение heartbeat делает процесс недоступным для новых подключений/назначений.'),
    'checkpoint':('Сохранить checkpoint забега','SHA-256 считается по точным UTF-8 байтам snapshotJson. Тик монотонен; schema v6 дополнительно использует checkpointSequence и допускает обновление паузы/retention на том же тике только с большей последовательностью. Schema v4/v5 запрещают разные snapshot на одном тике. Для реального восстановления нужен полный RunCheckpointData из Unity.'),
    'recover':('Восстановить забег из checkpoint','Сохраняются закреплённые версии, оружие и бонусы. Успешное восстановление повышает generation. При recoveryPending=true (202) подождите и повторите запрос.'),
    'abandon':('Закрыть назначенный забег без результата','Закрывает текущий READY-забег сервера без наград. Возврат энергии этим методом не выполняется; отмена непринятого назначения обрабатывается отдельно по таймауту.'),
    'results':('Подтвердить итог dedicated-забега','Только владеющий забегом сервер. Результат, XP, подарки и outbox сохраняются атомарно. Точный повтор возвращает прежнее начисление, изменённый результат даёт 409. Клиентский LOCAL сдаётся отдельным finish.'),
}
for suffix,(summary,description) in SERVER_DESCRIPTIONS.items():
    model=r.ServerAssignmentResponse if suffix in ('assignment','recover') else r.RunStatusResponse if suffix=='results' else r.Acknowledgement
    samples=({'empty':example({'assigned':False},'Пока не назначен'),'assigned':example(SERVER_ASSIGNED,'Назначение процесса')} if suffix in ('assignment','recover') else
        {'result':example({**ex.LOCAL_ACCEPTED['settlement'],'rewardStatus':'PENDING','ratingStatus':'PENDING','rating':ex.LOCAL_ACCEPTED['rating'],'analyticsStatus':'PENDING','analytics':ex.PENDING,'replayed':False},'Итог записан')} if suffix=='results' else
        {'ack':example({'status':'ready','replayed':False} if suffix=='ack' else {'status':'ready'} if suffix=='ready' else {'current':True} if suffix=='heartbeat' else {'accepted':True,'simulationTick':1200} if suffix=='checkpoint' else {'status':'abandoned'},'Подтверждение')})
    OPERATIONS[('GET' if suffix=='assignment' else 'POST','/internal/servers/{server_id}/'+suffix)]=operation(
        TAGS[8]['name'],summary,description,model,samples,accepted={'wait':example({'assigned':False,'recoveryPending':True},'Старое поколение ещё отвечает')} if suffix=='recover' else None)

ERRORS={
    401:('Нужна корректная авторизация: токен игрока или соответствующий внутренний секрет.','InvalidCredentials','Credential is invalid or expired'),
    404:('Объект не найден или не принадлежит аккаунту.','ResourceNotFound','Resource not found'),
    409:('Конфликт версии/поколения/активной операции либо изменение сохранённого намерения.','ResourceConflict','Request conflicts with saved state'),
    422:('Неверный формат или неподдержанная конфигурация. Проверьте errors и описание полей.','InvalidRequest','Request validation failed'),
    503:('Временная недоступность БД, зависимого сервиса, правил или совместимого игрового процесса. Повторите исходное намерение.','ServiceUnavailable','Service is temporarily unavailable'),
    500:('Неожиданная ошибка. Сохраните requestId; финансовые операции повторяйте с прежним requestKey.','InternalError','An internal error occurred'),
}


def configure_router(router):
    """Enrich declarations before include_router builds response fields for OpenAPI."""
    for route in router.routes:
        if not isinstance(route,APIRoute):continue
        for method in route.methods:
            spec=OPERATIONS.get((method,route.path))
            if spec is None:continue
            route.tags=list(spec['tags']);route.summary=spec['summary'];route.description=spec['description']
            route.operation_id=route.name
            route.responses=deepcopy(spec['responses'])
            requests=REQUEST_EXAMPLES.get((method,route.path))
            if requests:
                route.openapi_extra={'requestBody':{'content':{'application/json':{'examples':deepcopy(requests)}}}}
            errors=set(spec['errors'])|{500}
            if route.body_field or route.dependant.path_params or route.dependant.query_params:errors.add(422)
            for status in sorted(errors):
                description,code,detail=ERRORS[status]
                value={'code':code,'detail':detail,'requestId':ex.KEY,'errors':[]}
                if status==422:
                    if route.dependant.path_params and route.dependant.path_params[0].name!='release_id':
                        issue={'field':'path.'+route.dependant.path_params[0].name,'type':'uuid_parsing','message':'Invalid UUID'}
                    elif route.path == '/v1/profile/display-names':
                        issue={'field':'query.accountId','type':'uuid_parsing','message':'Invalid UUID'}
                    elif route.dependant.query_params:
                        issue={'field':'query.'+route.dependant.query_params[0].name,'type':'int_parsing','message':'Must be an integer'}
                    else:
                        body=REQUEST_EXAMPLES.get((method,route.path),{})
                        first=next(iter(body.values()),{}).get('value',{})
                        field='requestKey' if 'requestKey' in first else next(iter(first),'request')
                        issue={'field':'body.'+field,'type':'missing','message':'Required field is missing'}
                    value['errors']=[issue]
                route.responses[status]=response(r.ErrorResponse,description,{'error':example(value,description)})
                if status==409 and route.path in ('/v1/runs','/v1/local-runs'):
                    route.responses[status]['content']['application/json']['examples']['insufficientEnergy']=example(
                        {'code':'InsufficientEnergy','detail':'Insufficient energy','requestId':ex.KEY,'errors':[]},
                        'Не хватает энергии: дождитесь восстановления или пополните её')
                if status==503:route.responses[status]['headers']['Retry-After']={'description':'Рекомендуемая задержка, секунды.','schema':{'type':'string'},'example':'1'}


REQUEST_EXAMPLES={
    ('POST','/v1/runs'):{'start':example(ex.START_RUN,'Новое намерение запуска')},
    ('POST','/v1/local-runs'):{'start':example(ex.START_LOCAL,'Запустить по опубликованной policy')},
    ('POST','/v1/local-runs/{run_id}/finish'):{'victory':example(ex.FINISH_LOCAL,'Победа'),
        'defeat':example({**ex.FINISH_LOCAL,'outcome':'defeat','churchLevel':1,'stats':{'heroKills':{},'towerKills':{},'buildingUpgrades':{}}},'Поражение без боевых счётчиков')},
    ('PUT','/v1/profile'):{'rename':example(ex.PROFILE_UPDATE,'Ник, аватар и настройки')},
    ('PATCH','/v1/profile'):{'edit':example(ex.PROFILE_UPDATE,'Обновить поля'),'resetAvatar':example({'expectedVersion':2,'avatarAssetKey':None},'Сбросить аватар')},
    ('POST','/v1/cases/{case_id}/open'):{'pay':example({'requestKey':ex.KEY,'paymentMode':'WALLET'},'Оплатить престижем'),
        'consume':example({'requestKey':ex.KEY,'paymentMode':'INVENTORY'},'Потратить свой кейс')},
    ('POST','/v1/level-rewards/{claim_id}/claim'):{'claim':example({},'Тело можно также не передавать')},
    ('POST','/v1/prestige-upgrades/purchases'):{'buy':example(ex.BUY_PRESTIGE,'Купить первый уровень')},
    ('PUT','/internal/content/{release_id}'):{'publish':example(ex.PUBLICATION,'Сокращённый пример оболочки','Для игрового сервера используйте полный экспорт Unity, а не эту оболочку.')},
}
for suffix in SERVER_DESCRIPTIONS:
    if suffix=='assignment':continue
    value=ex.READY if suffix in ('ready','recover') else ex.CHECKPOINT if suffix=='checkpoint' else ex.RESULT if suffix=='results' else ex.ACK
    REQUEST_EXAMPLES[('POST','/internal/servers/{server_id}/'+suffix)]={'request':example(value,'Пример серверного запроса')}


def install_openapi(app):
    native=app.openapi
    def documented_openapi():
        document=native()
        # FastAPI's OpenAPI encoder excludes None recursively, including within Example.value.
        # Restore raw JSON examples so null (avatar reset, absent settlement) remains meaningful.
        for (method,path),spec in OPERATIONS.items():
            operation=document['paths'][path][method.lower()]
            for status,metadata in spec['responses'].items():
                operation['responses'][str(status)]['content']['application/json']['examples']=deepcopy(
                    metadata['content']['application/json']['examples'])
            if (method,path) in REQUEST_EXAMPLES:
                operation['requestBody']['content']['application/json']['examples']=deepcopy(REQUEST_EXAMPLES[(method,path)])
        return document
    app.openapi=documented_openapi
