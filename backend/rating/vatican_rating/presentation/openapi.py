"""Human-facing HTTP documentation and synthetic, executable examples."""
from copy import deepcopy
from uuid import NAMESPACE_URL, UUID, uuid5

from fastapi.routing import APIRoute
from ..application.catalog import MAX_SCORE
from .schemas import ErrorResponse

RATING_TAG = "Рейтинг игроков"
INTERNAL_TAG = "Внутренние операции"
HEALTH_TAG = "Проверка сервиса"

TAGS = [
    {"name": RATING_TAG, "description": "Доступные доски, таблица лидеров и место текущего игрока. Чтение не создаёт участников."},
    {"name": INTERNAL_TAG, "description": "Для доверенных серверов. Требуется X-Service-Secret. "
        "Игровой клиент не должен получать этот секрет или самостоятельно назначать очки."},
    {"name": HEALTH_TAG, "description": "Проверки HTTP-процесса и готовности базы данных без авторизации."},
]

DESCRIPTION = """
Сервис хранит рейтинговые доски, суммирует XP завершённых забегов и возвращает позиции игроков.
Очки рассчитывает **game_backend**, а rating сохраняет начисление однократно.

### Быстрый старт
1. Откройте **GET /v1/leaderboards** → **Try it out** → **Execute**: получите коды доступных досок.
   Стандартная доска XP — `run-xp`.
2. В **GET /v1/leaderboards/{code}** укажите `run-xp`. `limit` — размер страницы, `offset` — число пропущенных игроков.
   Список отсортирован по убыванию очков; при равенстве раньше идёт меньший UUID. Места начинаются с **1** и не повторяются.
3. Для **GET /v1/leaderboards/{code}/me** нажмите **Authorize → HTTPBearer** и вставьте **только токен**,
   без слова `Bearer`. Подходит `token` из `POST /v1/guests` сервиса auth либо `accessToken` из
   `POST /v1/auth/login`. `refreshToken` для этого не подходит. Чужой accountId выбрать нельзя.
4. Внутреннее начисление защищено отдельно: **Authorize → ServiceSecret** принимает значение
   `VD_SERVICE_SECRET`, добавляя заголовок `X-Service-Secret`. В примерах только вымышленные UUID, секретов нет.

### Как читать ответы
- Пустая доска: `entries: []`. Игрок ещё не участвовал: `entry: null`.
  Игрок с **0 XP** имеет запись и место. Неизвестная доска даёт **404**.
- Числа очков — целые от 0 до 9223372036854775807. JavaScript-клиентам для больших значений
  нужен JSON-парсер с поддержкой целых чисел без потери точности.
- Ошибки всегда имеют поля `code`, `detail`, `requestId`, `errors`; пояснения API на английском.
  `X-Request-ID` совпадает с `requestId` ошибки. Можно передать свой UUID в этом заголовке для поиска в логах.
  При **503** возвращается `Retry-After: 1`: повторите запрос позднее с теми же идентификаторами.

### HTTP и RabbitMQ
**POST /internal/messages** — синхронный HTTP-способ доставки: ответ `APPLIED` означает сохранённое начисление.
Это альтернатива доставке через RabbitMQ. Для повтора сохраните `messageId`, `runId` и содержимое payload;
новый забег должен иметь новые идентификаторы. Изменённое содержимое уже обработанного сообщения или забега даёт **409**.
`receipt.scoreAfter` — историческая сумма после первого начисления, а не текущий счёт.

При RabbitMQ команда сначала сохраняется в inbox, затем `rating_worker` начисляет очки и публикует результат.
**GET /internal/messages/{message_id}** показывает только этот AMQP-процесс. HTTP-начисление не создаёт inbox,
поэтому для него проверка AMQP-статуса вернёт **404**. `APPLIED` в rating и `PUBLISHED` события ещё не гарантируют
`COMPLETED` в game_backend: результат должен обработать `game_rating_results`.

RabbitMQ-команды не являются HTTP-методами и не отправляются через Try it out.
Создание доски выполняется CLI `vatican_rating.cli.configure_board`, ручной replay — CLI
`vatican_rating.cli.replay_broker`. Подробности: файлы `backend/rating/README.md` и `backend/docs/rating/RABBITMQ.ru.md` в репозитории.
"""

ACCOUNT_ID = "11111111-1111-4111-8111-111111111111"
MESSAGE_ID = "33333333-3333-4333-8333-333333333333"
RUN_ID = "22222222-2222-4222-8222-222222222222"
REQUEST_ID = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
BOARD_ID = str(uuid5(NAMESPACE_URL, "vaticandefendor:rating:run-xp"))
COMMAND = {
    "messageId": MESSAGE_ID, "eventName": "run.rating", "schemaVersion": 1,
    "payload": {"accountId": ACCOUNT_ID, "runId": RUN_ID, "boardCode": "run-xp",
                "rulesetId": "xp-v1", "contentReleaseId": "sha256-" + "a" * 64, "points": 80},
}
ZERO_COMMAND = deepcopy(COMMAND)
ZERO_COMMAND.update(messageId="66666666-6666-4666-8666-666666666666")
ZERO_COMMAND["payload"].update(accountId="44444444-4444-4444-8444-444444444444",
                              runId="55555555-5555-4555-8555-555555555555", points=0)
APPLIED = {"messageId": MESSAGE_ID, "status": "APPLIED",
           "receipt": {**COMMAND["payload"], "boardId": BOARD_ID, "scoreAfter": 80}}
ZERO_APPLIED = {"messageId": ZERO_COMMAND["messageId"], "status": "APPLIED",
                "receipt": {**ZERO_COMMAND["payload"], "boardId": BOARD_ID, "scoreAfter": 0}}
ENTRY = {"accountId": ACCOUNT_ID, "score": 80, "position": 1}
ZERO_ENTRY = {"accountId": ZERO_COMMAND["payload"]["accountId"], "score": 0, "position": 2}


def example(summary, value, description=None):
    result = {"summary": summary, "value": deepcopy(value)}
    if description:
        result["description"] = description
    return result


COMMAND_EXAMPLES = {
    "run80": example("Начислить 80 XP за новый забег", COMMAND,
                     "Доска run-xp уже создана миграцией. Для нового забега замените messageId и runId. "
                     "Повтор Execute с теми же данными безопасен: второй раз очки не прибавятся."),
    "runZero": example("Забег с 0 XP: игрок получит место", ZERO_COMMAND,
                       "Нулевой вклад допустим и создаёт участника. Это другой игрок и другой забег."),
}

PENDING = {"messageId": MESSAGE_ID, "status": "PENDING", "attempts": 0,
           "deliveryGeneration": 1, "lastError": None, "result": None, "events": []}


def broker_event(destination, status, attempts):
    return {"eventId": str(uuid5(UUID(MESSAGE_ID), f"rating.broker:{destination}:1")),
            "destination": destination, "attempts": attempts, "status": status}


BROKER_EXAMPLES = {
    "pending": example("Команда принята, worker ещё не начал обработку", PENDING),
    "applied": example("Очки сохранены, результат подтверждён RabbitMQ", {
        **PENDING, "status": "APPLIED", "attempts": 1, "result": APPLIED,
        "events": [broker_event("rabbit.results", "PUBLISHED", 1)]},
        "Подтверждение публикации не означает, что game_backend уже обработал результат."),
    "rejected": example("Ошибка содержимого: автоматического повтора нет", {
        **PENDING, "status": "REJECTED", "attempts": 1, "lastError": "CommandRejected",
        "result": {"messageId": MESSAGE_ID, "status": "REJECTED", "code": "CommandRejected"},
        "events": [broker_event("rabbit.results", "PENDING", 0), broker_event("rabbit.dead", "PENDING", 0)]}),
    "review": example("Исчерпаны 20 попыток, требуется проверка оператором", {
        **PENDING, "status": "REVIEW", "attempts": 20, "lastError": "HandlerUnavailable",
        "result": {"messageId": MESSAGE_ID, "status": "REVIEW", "code": "HandlerUnavailable"},
        "events": [broker_event("rabbit.results", "PENDING", 0), broker_event("rabbit.dead", "PENDING", 0)]}),
}

REQUEST_ID_HEADER = {"description": "UUID запроса для поиска в логах. Для ошибки совпадает с requestId в JSON.",
                     "schema": {"type": "string", "format": "uuid"}, "example": REQUEST_ID}


def success(model, description, examples):
    # New documentation models must not revalidate or rewrite historical response payloads.
    return {"model": model, "description": description, "headers": {"X-Request-ID": REQUEST_ID_HEADER},
            "content": {"application/json": {"examples": examples}}}


def failure(code, detail, issues=None):
    return {"code": code, "detail": detail, "requestId": REQUEST_ID, "errors": issues or []}


def error_responses(*statuses, credential="player", missing="board", auth=False, validation="pagination", readiness=False):
    """Document the installed error handlers instead of FastAPI's default 422 body."""
    descriptions = {
        400: "Некорректный Content-Length.",
        401: "Нет действующего токена игрока." if credential == "player" else "Нет верного X-Service-Secret. Проверяется до чтения JSON.",
        404: "Доска не существует." if missing == "board" else "Команда не найдена в AMQP inbox. HTTP-начисление не создаёт inbox.",
        408: "Тело запроса не получено за 5 секунд. Повторите то же сообщение.",
        409: "Конфликт неизменяемого сообщения/забега или переполнение суммы очков. Автоматический повтор содержимого не исправит конфликт.",
        413: "Тело запроса больше 16 384 байт (16 KiB), включая потоковую передачу.",
        422: "Неверные параметры, JSON или контракт команды. Исправьте запрос; подробности полей могут быть в errors.",
        500: "Непредвиденная ошибка сервера. Для диагностики сохраните X-Request-ID.",
        503: "База данных/схема временно недоступна, операция не уложилась в таймаут либо возник конфликт транзакций. "
             "Повторите запрос с теми же идентификаторами не раньше чем через 1 секунду."
             + (" Также возможна недоступность auth." if auth else ""),
    }
    values = {
        400: failure("InvalidRequest", "Bad Request"),
        401: failure("InvalidCredentials", "Bearer credential required" if credential == "player" else "Service credential required"),
        404: failure("ResourceNotFound", "Leaderboard not found" if missing == "board" else "Broker command not found"),
        408: failure("RequestTimeout", "Request Timeout"),
        409: failure("ResourceConflict", "Message payload changed"),
        413: failure("RequestTooLarge", "Request Entity Too Large"),
        422: failure("InvalidRequest", "Request validation failed", [
            {"field": "query.limit", "type": "less_than_equal", "message": "Value is outside the allowed range"}]),
        500: failure("InternalError", "An internal error occurred"),
        503: failure("DatabaseUnavailable", "Rating database is temporarily unavailable"),
    }
    result = {}
    for status in statuses:
        examples = {"error": example(descriptions[status], values[status])}
        if status == 503:
            examples.update({
                "schema": example("Миграции не готовы", failure("SchemaNotReady", "Rating schema is not ready")),
                "timeout": example("Таймаут операции", failure("OperationTimeout", "The database operation timed out")),
                "concurrent": example("Конфликт транзакций: запрос можно повторить", failure("ConcurrentModification",
                    "Another operation conflicted with this request; retry the operation")),
            })
            if auth:
                examples["auth"] = example("Auth недоступен", failure("ServiceUnavailable", "Auth service is unavailable"))
            if readiness:
                examples["revisionMismatch"] = example("Ревизия схемы не совпадает с приложением",
                    failure("ServiceUnavailable", "Database schema is not ready"))
        if status == 409:
            examples["runConflict"] = example("Изменился payload уже начисленного забега",
                failure("ResourceConflict", "Run rating source payload changed"))
            examples["capacity"] = example("Сумма превысила максимальное целое число",
                failure("ResourceConflict", "Leaderboard score capacity exceeded"))
        if status == 422 and validation in {"board", "ranking"}:
            if validation == "board":
                examples = {}
            examples["invalidCode"] = example("Неверный код доски", failure("InvalidRequest", "Invalid leaderboard code"))
        if status == 422 and validation == "message":
            examples = {
                "invalidContract": example("Неподдерживаемая команда/версия или неверный payload",
                    failure("InvalidRequest", "Invalid run rating message")),
                "unavailableBoard": example("Доска не существует или источник не RUN_XP", failure("InvalidRequest",
                    "Leaderboard is unavailable or has an incompatible score source")),
                "invalidVersion": example("schemaVersion должен быть целым числом", failure("InvalidRequest",
                    "Request validation failed", [{"field": "body.schemaVersion", "type": "int_type", "message": "Must be an integer"}])),
            }
        if status == 422 and validation == "broker":
            examples = {"invalidId": example("message_id должен быть UUID", failure("InvalidRequest",
                "Request validation failed", [{"field": "path", "type": "uuid_parsing", "message": "Invalid UUID"}]))}
        headers = {"X-Request-ID": REQUEST_ID_HEADER}
        if status == 401:
            headers["WWW-Authenticate"] = {"description": "Challenge единого обработчика ошибок HTTP.",
                                          "schema": {"type": "string"}, "example": "Bearer"}
        if status == 503:
            headers["Retry-After"] = {"description": "Через сколько секунд повторить запрос.", "schema": {"type": "integer"}, "example": 1}
        result[status] = {"model": ErrorResponse, "description": descriptions[status], "headers": headers,
                          "content": {"application/json": {"examples": examples}}}
    return result


def install_openapi(app):
    default_openapi = app.openapi

    def documented_openapi():
        if app.openapi_schema is None:
            spec = default_openapi()
            # FastAPI's OpenAPI encoder excludes None even inside examples. These nulls
            # are real, required response fields (unranked player / unfinished command).
            for route in app.routes:
                if not isinstance(route, APIRoute) or not route.include_in_schema:
                    continue
                for method in route.methods:
                    operation = spec["paths"][route.path_format][method.lower()]
                    for status, response in route.responses.items():
                        for media_type, content in response.get("content", {}).items():
                            if "examples" in content:
                                operation["responses"][str(status)]["content"][media_type]["examples"] = deepcopy(content["examples"])
            # OpenAPI's intermediate Schema model casts bounds to float, rounding int64's
            # maximum up by one. Keep the exact business limit in the published JSON.
            components = spec["components"]["schemas"]
            components["Message"]["properties"]["payload"]["properties"]["points"]["maximum"] = MAX_SCORE
            components["RankedEntry"]["properties"]["score"]["maximum"] = MAX_SCORE
            for name in ("points", "scoreAfter"):
                components["RunRatingReceipt"]["properties"][name]["maximum"] = MAX_SCORE
        return app.openapi_schema

    app.openapi = documented_openapi
