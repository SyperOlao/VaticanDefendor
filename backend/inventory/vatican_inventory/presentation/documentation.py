"""Human-facing OpenAPI metadata, kept separate from HTTP handlers."""
from copy import deepcopy
from fastapi.encoders import jsonable_encoder
from fastapi.routing import APIRoute

from . import examples as ex
from .schemas import (ApiError, MessageResult, BrokerCommandStatus, BrokerCommandsPage,
                      BrokerEventsPage, BrokerEventSummary, BrokerRejectionsPage, BrokerRejectionSummary)

DESCRIPTION = """
Инвентарь игрока: предметы, кейсы, экземпляры оружия, их опыт и экипировка.
Определения оружия предоставляет **game_backend**, пользователя проверяет **auth**.

Здесь описан **HTTP API** для клиента и ручной работы. В общем Compose game_backend/shop
передают команды через RabbitMQ, а снимок и сохранённые результаты читают через gRPC
InventoryReads (отдельный порт 28093, контракт contracts/inventory/v1/inventory.proto).

## Как начать в Swagger

1. Получите токен в auth: в общем Compose по умолчанию [Swagger auth](http://127.0.0.1:28081/docs)
   (в другом окружении используйте адрес своего auth),
   `POST /v1/guests` → поле `token` или вход `POST /v1/auth/login` → `accessToken`.
2. Нажмите **Authorize → HTTPBearer** и вставьте **только токен**, без слова `Bearer`.
3. Откройте **«Оружие» → «Получить страницу экземпляров оружия»**, нажмите **Try it out → Execute**.
   Аккаунт определяется токеном; передавать accountId в клиентские методы не нужно.
4. Для экипировки возьмите реальный `InstanceId` из ответа, текущую `Revision` и новый UUID `requestKey`.
   Примеры ниже используют вымышленные UUID; заменяйте их своими.

## Два вида доступа

| Раздел | Что указать в Authorize |
| --- | --- |
| Клиентские `/v1/*` | **HTTPBearer** — токен auth |
| Сервисные `/internal/*` | **ServiceSecret** — значение `VD_SERVICE_SECRET`, передаётся как `X-Service-Secret` |
| `/health/*` | Авторизация не требуется |

Сервисный секрет предназначен для доверенных серверов и не должен попадать в игровой клиент.
Значения авторизации не сохраняются Swagger после перезагрузки страницы.

## Экземпляр оружия и его определение

`ConfigurationId`, например `sword.common`, — вид оружия. `InstanceId` — UUID конкретного
принадлежащего игроку экземпляра с собственным опытом. В экипировку передаются **InstanceId**.
`GET /v1/weapons/owned` читает экземпляры без каталога и работает при недоступном game_backend.
Полное чтение, карточка оружия, новый выбор экипировки и новые выдачи оружия требуют доступных определений.

## Страницы, ревизии и повторы

- Страницы: `limit` от 1 до 100, затем `cursor` из `nextCursor` с прежним фильтром.
  `nextCursor: null` — конец. Курсор нельзя переносить другому игроку или другому фильтру.
- Если инвентарь изменился между страницами, сервер вернёт **409**: начните обход заново без cursor.
- `expectedRevision` защищает новый выбор экипировки от устаревшего состояния.
  Для повторной отправки одной операции сохраняйте прежние UUID и всё тело запроса.
- Повтор с `requestKey` возвращает исходный ответ, даже если экипировка уже изменилась позже.
  Для нового выбора нужен новый requestKey. Текущее состояние читайте отдельным GET.
- Сохранённые выдачи и списания возвращают исходный результат; новый messageId с тем же источником
  награды не повторяет выдачу. Изменение исходного содержимого с прежним ключом даёт **409**.

## HTTP и RabbitMQ

`POST /internal/messages` **синхронно выполняет** команду. Это не HTTP-метод постановки в RabbitMQ.
Тот же JSON можно опубликовать в direct exchange `vatican.commands`, routing key `inventory`.
Worker сохраняет команду перед ACK; результаты публикует в `vatican.results`, ошибки — в `vatican.dead`.
Ресурсы приведены для настроек по умолчанию. Состояние RabbitMQ-команд доступно в разделе диагностики.
HTTP-only вызовы не создают записи broker inbox.

**HTTP 200 и внешний status=APPLIED означают обработку команды.** У списания предмета и выдачи
покупки проверяйте `receipt.status`, у `case.consume` — `receipt.outcome`.
Нехватка предметов/кейсов — сохранённый бизнес-отказ; повтор после пополнения не изменит этот отказ.

Ошибки HTTP имеют единый формат `code`, `detail`, `requestId`, `errors`.
`requestId` также возвращается в `X-Request-ID`; значения detail/message остаются на английском.
"""

TAGS = [
    {"name": "Оружие", "description": "Чтение экземпляров и определений, выбор экипировки и подтверждение операции."},
    {"name": "Предметы и кейсы", "description": "Остатки предметов и кейсов текущего игрока, включая нулевые остатки."},
    {"name": "Сервисные операции", "description": "Выдача покупки, расходование предмета и снимок забега. Требуется ServiceSecret."},
    {"name": "Команды", "description": "Шесть команд выдачи и списания через единый синхронный HTTP-контракт."},
    {"name": "Диагностика RabbitMQ", "description": "Исполнение команд, публикация результатов и карантин. Только чтение, требуется ServiceSecret."},
    {"name": "Проверка сервиса", "description": "Liveness процесса и readiness PostgreSQL/миграций без авторизации."},
]

ERRORS = {
    401: ("Нет подходящего токена или сервисного секрета", "InvalidCredentials", "Bearer credential required"),
    403: ("Попытка выбрать чужое оружие", "PermissionDenied", "Weapon is not owned"),
    404: ("Запись не найдена или не принадлежит указанному владельцу", "ResourceNotFound", "Weapon not found"),
    409: ("Конфликт ревизии или изменённое тело прежней операции", "ResourceConflict", "Inventory revision changed"),
    422: ("Некорректные поля, формат UUID, фильтр, курсор или тело команды", "InvalidRequest", "Request validation failed"),
    500: ("Внутренняя ошибка; для диагностики сохраните requestId", "InternalError", "An internal error occurred"),
    503: ("Недоступна БД/зависимый сервис либо не применены миграции", "ServiceUnavailable", "Game content is unavailable"),
}


def _error(status):
    description, code, detail = ERRORS[status]
    value = {"code": code, "detail": detail, "requestId": ex.OPERATION, "errors": []}
    if status == 422:
        value["errors"] = [{"field": "body.quantity", "type": "greater_than_equal", "message": "Value is outside the allowed range"}]
    examples = {"error": ex.example(description, value)}
    if status == 401:
        examples["serviceSecret"] = ex.example("Отсутствует или неверен X-Service-Secret", {**value, "detail": "Service credential required"})
    if status == 409:
        examples["pageChanged"] = ex.example("Инвентарь изменился во время обхода страниц", {**value, "detail": "Inventory changed; restart pagination"})
        examples["payloadChanged"] = ex.example("Ключ операции уже использован с другим телом", {**value, "detail": "Operation payload changed"})
    if status == 503:
        examples["database"] = ex.example("PostgreSQL временно недоступна", {**value, "code": "DatabaseUnavailable", "detail": "Inventory database is temporarily unavailable"})
        examples["schema"] = ex.example("Миграции не готовы", {**value, "code": "SchemaNotReady", "detail": "Inventory schema is not ready"})
    result = {"model": ApiError, "description": description, "content": {"application/json": {"examples": examples}}}
    if status == 503:
        result["headers"] = {"Retry-After": {"description": "Рекомендуемая задержка перед повтором, секунды.", "schema": {"type": "string", "example": "1"}}}
    return result


def _operation(tag, summary, description, examples, *, errors=(401, 422, 500, 503), model=None):
    response = {"description": "Результат запроса; выберите вариант ответа в Examples.", "content": {"application/json": {"examples": examples}}}
    if model is not None:
        response["model"] = model  # Documentation only; business response serialization stays unchanged.
    return {"tags": [tag], "summary": summary, "description": description,
            "responses": {200: response, **{status: _error(status) for status in errors}}}


READ_ERRORS = (401, 404, 422, 500, 503)
PAGE_ERRORS = (401, 409, 422, 500, 503)
OPERATIONS = {
    "mock_grant": _operation('TEST/dev MOCK', 'Выдать одно оружие по проверенной TEST-транзакции',
        'Метод существует только при VD_INVENTORY_MOCK_PAYMENTS_ENABLED=1 в test/development. '
        'В Authorize заполните **оба** поля: ServiceSecret и MockGrantSecret; нужны заголовки X-Service-Secret и X-Mock-Grant-Key. '
        'Источник — transactionId/lineIndex/unitIndex, глобальный для всех аккаунтов. '
        'Повтор того же тела возвращает исходный экземпляр без новой выдачи и без обращения к каталогу; '
        'изменение владельца или конфигурации с прежним источником даёт 409. '
        'Неизвестная конфигурация — 422; недоступный каталог — 503. Это отдельный TEST-контракт, не обычная покупка и не RabbitMQ-команда.',
        {'applied': ex.example('Выданный экземпляр и сохранённое подтверждение', ex.MOCK_GRANTED)},
        errors=(401, 409, 422, 500, 503)),
    "live": _operation("Проверка сервиса", "Проверить, что HTTP-процесс работает",
        "Быстрая проверка процесса без обращения к PostgreSQL, auth и RabbitMQ. Не подтверждает готовность зависимостей.",
        {"live": ex.example("Процесс отвечает", ex.LIVE)}, errors=()),
    "ready": _operation("Проверка сервиса", "Проверить БД и версию миграций",
        "Возвращает 200, если PostgreSQL доступна и ревизия схемы совпадает с ожидаемой. "
        "При недоступной БД или несовпадении миграций — 503. Не проверяет auth, каталог game_backend и RabbitMQ worker.",
        {"ready": ex.example("Схема готова", ex.READY)}, errors=(500, 503)),
    "inventory": _operation("Оружие", "Получить всё оружие с определениями",
        "Полная коллекция игрока, определения game_backend, экипировка и ревизия. При первом чтении создаётся стартовый меч. "
        "Требует доступного каталога; иначе 503. Для больших коллекций или чтения без каталога используйте `/v1/weapons/owned`.",
        {"collection": ex.example("Один экземпляр и его определение", ex.WEAPONS)}),
    "owned_weapons": _operation("Оружие", "Получить страницу экземпляров оружия",
        "Работает без запроса к game_backend. Сортировка — по времени выдачи и UUID, от старых к новым. "
        "Loadout содержит весь текущий выбор, включая экземпляры на других страницах. "
        "Для продолжения передайте nextCursor как cursor с тем же configurationId. При изменении ревизии — 409: начните заново.",
        {"lastPage": ex.example("Последняя страница; каталог не требуется", ex.OWNED_PAGE)}, errors=PAGE_ERRORS),
    "weapon": _operation("Оружие", "Посмотреть один экземпляр и его определение",
        "Возвращает уровень/опыт конкретного InstanceId и его определение из game_backend. "
        "Чужой и неизвестный экземпляр дают 404; недоступный каталог — 503.",
        {"weapon": ex.example("Карточка экземпляра", {"Weapon": ex.OWNED_WEAPON, "Configuration": ex.DEFINITION, "Revision": 7})}, errors=READ_ERRORS),
    "loadout": _operation("Оружие", "Выбрать оружие для следующих забегов",
        "Передайте от 1 до 3 различных принадлежащих игроку InstanceIds в порядке слотов. "
        "expectedRevision берите из свежего GET. Устаревшая ревизия даёт 409, чужой экземпляр — 403. "
        "Неизменённый выбор не повышает ревизию. С requestKey повтор возвращает **исходный ответ**, "
        "не применяет старый выбор заново и работает без каталога. Для нового выбора создайте новый requestKey; "
        "его повтор с другим телом даёт 409. Уже начатый забег использует свой закреплённый снимок.",
        {"keyed": ex.example("Выбор с подтверждением операции", {**ex.WEAPONS, "loadoutOperation": ex.LOADOUT_RESULT}),
         "withoutKey": ex.example("Выбор без requestKey", ex.WEAPONS)}, errors=(401, 403, 409, 422, 500, 503)),
    "loadout_result": _operation("Оружие", "Найти подтверждение смены экипировки",
        "По requestKey возвращает исходные InstanceIds и ревизию операции текущего игрока. Не обращается к каталогу "
        "и не меняет выбор. Текущая экипировка могла измениться позже. Чужой или неизвестный ключ — 404.",
        {"receipt": ex.example("Сохранённый выбор", ex.LOADOUT_RESULT)}, errors=READ_ERRORS),
    "items": _operation("Предметы и кейсы", "Получить предметы и кейсы игрока",
        "Без query-параметров возвращает всю коллекцию в прежнем формате items/revision. "
        "Передача limit, cursor или kind включает страницы (размер по умолчанию 50) и поле nextCursor. "
        "kind=ITEM выбирает ключи item:*, kind=CASE — case:*; без фильтра видны и legacy-ключи. "
        "Сортировка — по itemId. Нулевые остатки остаются в ответе. При изменении ревизии между страницами — 409.",
        {"legacy": ex.example("Полный ответ без query-параметров", ex.ITEMS),
         "filteredPage": ex.example("Последняя страница kind=ITEM", ex.ITEMS_PAGE)}, errors=PAGE_ERRORS),
    "item": _operation("Предметы и кейсы", "Проверить остаток одного предмета или кейса",
        "Читает исходный itemId, например item:repair-kit или case:daily, и текущую ревизию. "
        "Если сохранённый остаток равен нулю — возвращает 200 с quantity=0. Если строки нет — 404.",
        {"balance": ex.example("Есть 4 предмета", ex.ITEM), "empty": ex.example("Предмет израсходован", {**ex.ITEM, "quantity": 0})}, errors=READ_ERRORS),
    "snapshot": _operation("Сервисные операции", "Закрепить данные выбранного оружия для забега",
        "Возвращает только выбранные экземпляры в Weapons, их Loadout, Revision и каталог Configurations. "
        "Игровой сервис сохраняет **весь ответ** до начала забега и передаёт его неизменным в snapshot команды run.weapon_reward. "
        "Вызов не создаёт забег и не начисляет XP; требует доступного game_backend.",
        {"snapshot": ex.example("Сохраните этот JSON как snapshot", ex.WEAPONS)}),
    "grant": _operation("Сервисные операции", "Выдать один экземпляр оружия за покупку",
        "Ключ выдачи — accountId/operationId. Повтор с той же конфигурацией возвращает исходный экземпляр и ревизию, "
        "даже если каталог позже недоступен. Другая configurationId с тем же ключом — 409. "
        "Неизвестная конфигурация сохраняется как REJECTED/UnknownWeaponConfiguration с HTTP 200. "
        "Сетевой сбой каталога даёт 503 и допускает повтор прежней операции. Эквивалентная команда — inventory.weapon.grant.",
        {"granted": ex.example("Оружие выдано", ex.GRANTED), "rejected": ex.example("Сохранён бизнес-отказ (HTTP 200)", ex.GRANT_REJECTED)}, errors=PAGE_ERRORS),
    "grant_result": _operation("Сервисные операции", "Проверить результат выдачи покупки",
        "Возвращает сохранённый APPLIED/REJECTED для указанного игрока и operationId, без запроса каталога и без новой выдачи. "
        "404 означает отсутствие результата на момент чтения и **не доказывает отмену параллельного запроса**. "
        "После потери ответа повторите прежний POST с тем же ключом и телом.",
        {"receipt": ex.example("Подтверждение покупки", ex.GRANTED)}, errors=READ_ERRORS),
    "consume": _operation("Сервисные операции", "Списать обычный предмет с защитой от повторов",
        "Inventory только списывает количество; разрешение и применение игрового эффекта — ответственность вызывающего сервиса. "
        "Ключ — accountId/operationId, общий для HTTP и inventory.item.consume. "
        "Нехватка сохраняется как REJECTED/InsufficientItems с HTTP 200 и остаётся отказом после пополнения. "
        "Изменение itemId/quantity с прежним ключом — 409. Для case:* используйте case.consume.",
        {"applied": ex.example("Списаны 2 предмета", ex.CONSUMED), "insufficient": ex.example("Сохранён отказ: недостаточно предметов (HTTP 200)", ex.INSUFFICIENT_ITEMS)}, errors=PAGE_ERRORS),
    "consumption": _operation("Сервисные операции", "Найти сохранённый результат списания",
        "Читает исходный результат accountId/operationId без нового списания. quantityAfter — остаток на момент операции, "
        "а не текущий баланс. 404 не доказывает отмену параллельного POST: при потере ответа повторяйте прежнюю команду.",
        {"applied": ex.example("Успешное списание", ex.CONSUMED), "rejected": ex.example("Исходный отказ", ex.INSUFFICIENT_ITEMS)}, errors=READ_ERRORS),
    "receive": _operation("Команды", "Выполнить команду выдачи или списания",
        """Выполняет команду синхронно. Выберите нужный вариант в **Request body → Examples**.

| eventName | Поля payload | Ключ бизнес-операции |
| --- | --- | --- |
| `level.reward` | accountId, claimId, itemIndex, rewardType, rewardRef, quantity | claimId/itemIndex |
| `case.reward` | accountId, openingId, itemIndex, rewardType, rewardRef, quantity | openingId/itemIndex |
| `case.consume` | accountId, openingId, caseCode, quantity=1 | openingId (списание кейса) |
| `inventory.item.consume` | accountId, operationId, itemId, quantity | accountId/operationId |
| `inventory.weapon.grant` | accountId, operationId, configurationId | accountId/operationId |
| `run.weapon_reward` | accountId, runId, configurationId, snapshot | runId |

Все команды используют schemaVersion=1. У наград rewardType — WEAPON, ITEM или CASE;
rewardRef передаётся **без** префикса item:/case:. quantity — положительное целое;
для WEAPON до 100 экземпляров. itemIndex — целое число от 0.
У run.weapon_reward snapshot — исходный ответ внутреннего snapshot до начала забега;
он закрепляет выбранные экземпляры и правила начисления XP, а configurationId выбирает новое наградное оружие.

Для новой команды создайте новый messageId; при повторной доставке сохраните messageId и всё тело.
Новый messageId с прежним источником
возвращает исходный результат без повторного эффекта. Изменённый payload с прежним ключом — 409.
Внешний APPLIED означает обработку: проверяйте receipt.status у списания/покупки и receipt.outcome у case.consume.

Для асинхронной доставки опубликуйте тот же JSON в RabbitMQ (`vatican.commands`, routing key `inventory`),
а не вызывайте этот HTTP-метод. Размер AMQP-тела — до 256 KiB; content_type=application/json,
content_encoding=utf-8, delivery_mode=2; AMQP message_id, если задан, совпадает с messageId.
""",
        {"reward": ex.example("Награда выдана", ex.MESSAGE_RESULT),
         "insufficientItems": ex.example("Команда обработана, списание отклонено (HTTP 200)", ex.MESSAGE_REJECTED_ITEMS),
         "insufficientCases": ex.example("Команда обработана, кейса не хватило (HTTP 200)", ex.MESSAGE_REJECTED_CASE)},
        errors=(401, 409, 422, 500, 503), model=MessageResult),
    "broker_status": _operation("Диагностика RabbitMQ", "Проверить исполнение команды и доставку результатов",
        "Читает durable inbox по messageId. status команды и status её событий — разные этапы: "
        "APPLIED с событием PENDING ещё не означает доставленный результат. result содержит исходный receipt; "
        "проверяйте его бизнес-статус. Для HTTP-only команды записи здесь нет. 404 не означает отмену доставки, "
        "которая ещё не сохранена receiver. Все времена/ошибки — безопасные метаданные.",
        {"applied": ex.example("Команда применена, результат опубликован", ex.COMMAND_STATUS),
         "pending": ex.example("Команда ожидает обработки", {**ex.COMMAND_STATUS, "status": "PENDING", "attempts": 0, "result": None, "events": []})},
        errors=READ_ERRORS, model=BrokerCommandStatus),
    "broker_commands": _operation("Диагностика RabbitMQ", "Найти принятые команды по статусу",
        "От новых к старым по createdAt/UUID. status необязателен: PENDING, APPLIED, REJECTED или REVIEW. "
        "REVIEW требует оператора после 20 попыток. Передавайте cursor с тем же status. "
        "Список отражает текущее состояние: статусы меняются во время обхода, поэтому периодически начинайте с первой страницы. "
        "Исходные envelope/payload не выводятся. Для результата отдельной команды используйте метод по messageId.",
        {"applied": ex.example("Принятые команды", {"commands": [ex.COMMAND_SUMMARY], "nextCursor": None}),
         "review": ex.example("Команда требует разбора", {"commands": [{**ex.COMMAND_SUMMARY, "status": "REVIEW", "attempts": 20, "lastError": "HandlerUnavailable"}], "nextCursor": None})},
        model=BrokerCommandsPage),
    "broker_events": _operation("Диагностика RabbitMQ", "Найти публикации результатов и dead-letter",
        "Показывает outbox inventory: результаты, dead-letter и карантин. status — PENDING, PUBLISHED или REVIEW. "
        "PUBLISHED означает publisher confirm RabbitMQ, а не обработку получателем. REVIEW — исчерпаны попытки публикации. "
        "Порядок от новых к старым; продолжайте cursor с тем же фильтром. Тела событий не выводятся.",
        {"review": ex.example("Непубликованное событие после 20 попыток", {"events": [ex.FAILED_EVENT], "nextCursor": None}),
         "published": ex.example("Опубликованные результаты", {"events": [ex.EVENT_SUMMARY], "nextCursor": None})}, model=BrokerEventsPage),
    "broker_event": _operation("Диагностика RabbitMQ", "Проверить одну публикацию outbox",
        "По eventId показывает направление, статус, попытки и срок аренды без payload. "
        "Повтор неуспешной публикации выполняется в окружении inventory командой "
        "`python -m vatican_inventory.cli.replay_broker <eventId> --outbox`; сохраняются eventId и исходное тело.",
        {"published": ex.example("Подтверждённая публикация", ex.EVENT_SUMMARY), "review": ex.example("Нужен операторский повтор", ex.FAILED_EVENT)},
        errors=READ_ERRORS, model=BrokerEventSummary),
    "broker_rejections": _operation("Диагностика RabbitMQ", "Найти некорректные и конфликтующие доставки",
        "Карантин содержит размер, hash и безопасный код без исходного тела. Необязательный code — BrokerCode, "
        "например InvalidEnvelope, MessageIdMismatch, MessageConflict или PayloadTooLarge. "
        "В malformed JSON может не быть messageId: используйте rejectionId. Поле event содержит связанный eventId для redrive. "
        "Порядок от новых к старым; cursor передаётся с прежним code.",
        {"quarantine": ex.example("Некорректное тело без messageId", {"rejections": [ex.REJECTION_SUMMARY], "nextCursor": None})}, model=BrokerRejectionsPage),
    "broker_rejection": _operation("Диагностика RabbitMQ", "Посмотреть доставку из карантина",
        "Возвращает метаданные по rejectionId без исходных байтов сообщения. bodyTruncated=true означает, "
        "что сохранён только префикс до 4096 байт. Для повтора доставки dead-letter используйте вложенный event.eventId "
        "с CLI replay_broker --outbox; это не повторное исполнение некорректного тела.",
        {"quarantine": ex.example("Карантин и его публикация", ex.REJECTION_SUMMARY)}, errors=READ_ERRORS, model=BrokerRejectionSummary),
}


_NOT_FOUND = {
    "weapon": "Weapon not found", "item": "Item not found",
    "loadout_result": "Loadout operation not found", "grant_result": "Purchase grant not found",
    "consumption": "Item consumption not found", "broker_status": "Broker command not found",
    "broker_event": "Broker record not found", "broker_rejection": "Broker record not found",
}


def operation(name):
    result = deepcopy(OPERATIONS[name])
    responses = result["responses"]
    if 401 in responses:
        internal = result["tags"][0] in {"Сервисные операции", "Команды", "Диагностика RabbitMQ", 'TEST/dev MOCK'}
        examples = responses[401]["content"]["application/json"]["examples"]
        responses[401]["content"]["application/json"]["examples"] = {
            "credential": examples["serviceSecret" if internal else "error"]}
        if name == 'mock_grant':
            value = examples['serviceSecret']['value']
            responses[401]['content']['application/json']['examples']['mockCredential'] = ex.example(
                'Отсутствует или неверен отдельный TEST grant-ключ', {**value, 'detail': 'Separate MOCK grant credential required'})
    if name in _NOT_FOUND:
        value = responses[404]["content"]["application/json"]["examples"]["error"]["value"]
        value["detail"] = _NOT_FOUND[name]
    if 409 in responses:
        examples = responses[409]["content"]["application/json"]["examples"]
        if name in {"owned_weapons", "items"}:
            examples = {"pageChanged": examples["pageChanged"]}
        elif name == "loadout":
            examples = {key: examples[key] for key in ("error", "payloadChanged")}
        else:
            value = examples["payloadChanged"]["value"]
            value["detail"] = {"grant": "Purchase configuration changed", "receive": "Message payload changed",
                               'mock_grant': 'MOCK payment source payload changed'}.get(name, "Operation payload changed")
            examples = {"payloadChanged": examples["payloadChanged"]}
        responses[409]["content"]["application/json"]["examples"] = examples
    if 422 in responses:
        value = responses[422]["content"]["application/json"]["examples"]["error"]["value"]
        if name in {"owned_weapons", "items", "broker_commands", "broker_events", "broker_rejections"}:
            value["errors"] = [{"field": "query.limit", "type": "greater_than_equal", "message": "Value is outside the allowed range"}]
        elif name in {"receive", "loadout"}:
            field = "schemaVersion" if name == "receive" else "expectedRevision"
            value["errors"] = [{"field": "body." + field, "type": "int_type", "message": "Must be an integer"}]
        elif name == "grant":
            value["errors"] = [{"field": "body.configurationId", "type": "string_pattern_mismatch", "message": "Invalid format"}]
        elif name == 'mock_grant':
            value['errors'] = [{'field': 'body.lineIndex', 'type': 'int_type', 'message': 'Must be an integer'}]
        elif name == "item":
            value.update(detail="Invalid item identifier", errors=[])
        elif name in _NOT_FOUND or name == "snapshot":
            field = {"weapon": "instance_id", "loadout_result": "request_key", "broker_status": "message_id",
                     "broker_event": "event_id", "broker_rejection": "rejection_id"}.get(name, "account_id")
            value["errors"] = [{"field": "path." + field, "type": "uuid_parsing", "message": "Invalid UUID"}]
    if name == "ready":
        examples = responses[503]["content"]["application/json"]["examples"]
        examples.pop("error")
        examples["wrongVersion"] = ex.example("Версия схемы не совпадает с ожидаемой", {
            "code": "ServiceUnavailable", "detail": "Database schema is not ready", "requestId": ex.OPERATION, "errors": []})
    return result


def configure_openapi(app):
    """Keep nullable examples intact and express the MOCK route's two-key requirement."""
    original = app.openapi
    def openapi():
        schema = original()
        # FastAPI's default exclude_none also strips required nulls inside examples.
        # Restore examples rather than making required response fields optional.
        for route in app.routes:
            if not isinstance(route, APIRoute) or not route.include_in_schema:continue
            for method in route.methods:
                operation = schema['paths'][route.path][method.lower()]
                for status, response in route.responses.items():
                    examples = response.get('content', {}).get('application/json', {}).get('examples')
                    if examples is not None:
                        operation['responses'][str(status)]['content']['application/json']['examples'] = jsonable_encoder(examples, exclude_none=False)
        path = schema['paths'].get('/internal/mock-payment-grants')
        if path is not None:
            path['post']['security'] = [{'ServiceSecret': [], 'MockGrantSecret': []}]
        return schema
    app.openapi = openapi
