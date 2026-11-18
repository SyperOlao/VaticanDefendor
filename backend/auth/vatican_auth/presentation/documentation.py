"""Human-facing HTTP API documentation; no authentication logic lives here."""
from copy import deepcopy

from ..application.catalog import PERMISSION_MAP, ROLE_MAP
from ..application.errors import AccountAlreadyRegistered, DelegationDenied, LoginUnavailable, RoleNotAssignable
from ..application.policy import AuthPolicy
from .errors import ERROR_RESPONSES
from .examples import REQUEST_ID, SUCCESS_EXAMPLES

TAGS = {
    "health": "Доступность", "accounts": "Аккаунты", "sessions": "Сессии",
    "recovery": "Восстановление", "roles": "Роли", "permissions": "Разрешения", "audit": "Аудит",
}
TAG_METADATA = [
    {"name": TAGS["health"], "description": "Проверка процесса, подключения к БД и готовности схемы. Авторизация не нужна."},
    {"name": TAGS["accounts"], "description": "Создание гостя или зарегистрированного аккаунта. Регистрация не выдаёт административные права."},
    {"name": TAGS["sessions"], "description": "Вход, текущий аккаунт, обновление токенов и выход. В Authorize вставляйте accessToken, а не refreshToken."},
    {"name": TAGS["recovery"], "description": "Восстановление доступа по recovery-коду, выданному при регистрации или предыдущем восстановлении."},
    {"name": TAGS["roles"], "description": "Администрирование ролей и их назначений. У каждого метода указано необходимое разрешение."},
    {"name": TAGS["permissions"], "description": "Каталог кодов прав. Роль объединяет разрешения; произвольный созданный код сам по себе не добавляет новую функцию серверу."},
    {"name": TAGS["audit"], "description": "История административных действий, включая отказы в доступе."},
]


def api_description(policy: AuthPolicy) -> str:
    return f"""Сервис аккаунтов **VaticanDefendor**: гостевой вход, регистрация, сессии,
восстановление доступа и управление ролями/разрешениями.

### Быстрая проверка
1. Выполните **GET /health/ready** — ожидается `200`.
2. Выполните **POST /v1/auth/register** с новым логином и паролем.
3. Сохраните `recoveryCode`. Скопируйте `accessToken`, нажмите **Authorize** и вставьте только значение токена, **без** `Bearer `.
4. Вызовите **GET /v1/auth/me** — увидите UUID аккаунта, роли и разрешения.
5. Для повторного входа используйте **POST /v1/auth/login**. Повторная регистрация занятого логина вернёт `409`.

### Токены и пароли
- Пароль при регистрации и восстановлении: **{policy.password_min_length}–{policy.password_max_length} символов**.
- `accessToken` действует **{int(policy.access_ttl.total_seconds())} секунд**, `refreshToken` — **{int(policy.refresh_ttl.total_seconds())} секунд**.
- Гостевой `token` действует **{int(policy.guest_ttl.total_seconds())} секунд**. Его тоже можно передать через **Authorize** для `/me` и `/logout`.
- Это opaque-токены, а не JWT: декодировать их не требуется.
- После **refresh** сохраните **оба новых токена** и обновите значение в **Authorize**. Старая пара больше не работает.
- **Logout** отзывает сессию на сервере. Swagger не очищает поле Authorize автоматически — для очистки нажмите **Logout** в окне Authorize.
- Восстановление меняет пароль и recovery-код, отзывает прежние сессии. Сохраните новый код.
- На гостевой вход, регистрацию, вход и восстановление действуют лимиты: **{policy.ip_attempt_limit}** попыток по IP и, когда передан логин, **{policy.login_attempt_limit}** по логину за **{int(policy.attempt_window.total_seconds())} секунд**.

### Административные методы
Обычный аккаунт не получает роль `{ROLE_MAP['admin'].code}` при регистрации.
Сначала зарегистрируйте аккаунт, затем оператор запускает команду:

```sh
docker compose exec auth python -m vatican_auth.cli.bootstrap_admin --login ваш_логин
```

Команда выполняется из `backend/infra/`. Без Docker:
`python -m vatican_auth.cli.bootstrap_admin --login ваш_логин` с настроенным окружением auth.
После назначения повторно вызовите `/me`: права применяются без повторного входа.
`401` означает отсутствие действительного токена, `403` — недостаток прав.

### Примеры и ошибки
Все UUID, ID, даты и токены в примерах **вымышленные**. Для запросов подставляйте значения из реальных ответов.
В теле запроса используются JSON-поля `camelCase`.
Ошибка содержит `code`, понятное `detail`, `requestId` и список `errors` для полей.
`requestId` совпадает с заголовком **X-Request-ID** и помогает найти ошибку в логах.
При `429` время ожидания указано в **Retry-After**, в секундах.
"""


# summary, tag, description, expected application-error HTTP statuses, success status
ENDPOINTS = {
    "live": ("Проверить, что процесс запущен", "health", "Без авторизации и тела запроса. Не проверяет подключение к базе; для этого используйте `/health/ready`.", (), 200),
    "ready": ("Проверить готовность сервиса и миграций", "health", "Без авторизации и тела запроса. `200` означает, что БД отвечает и ревизия схемы актуальна; `503` — сервис пока не готов.", (), 200),
    "create_guest": ("Начать игру без регистрации", "accounts", "Без авторизации и тела запроса. Создаёт UUID аккаунта и гостевой `token`. Для сохранения аккаунта привяжите логин через регистрацию с `guestToken`. Потерянный гостевой токен восстановить нельзя.", (400, 429), 201),
    "register": ("Зарегистрироваться или привязать логин к гостю", "accounts", "Авторизация не нужна. Выберите пример нового аккаунта или привязки гостя. Во втором случае передайте `guestToken` в JSON: UUID сохранится, а гостевой токен будет отозван. Логин регистронезависимый. Вернутся access/refresh-токены и recovery-код; административные роли не выдаются.", (400, 401, 409, 422, 429), 201),
    "login": ("Войти по логину и паролю", "sessions", "Авторизация не нужна. Создаёт отдельную сессию и возвращает пару токенов. Сохраните `accessToken` для Authorize и `refreshToken` для обновления. Неверный пароль и неактивный аккаунт возвращают `401`.", (400, 401, 422, 429), 200),
    "refresh": ("Однократно обновить пару токенов", "sessions", "Bearer-токен не нужен: передайте текущий `refreshToken` в JSON. Успех отзывает старую сессию, включая её access-токен. Сохраните оба новых токена и замените access-токен в Authorize. Повтор старого refresh вернёт `401`.", (401, 409, 422), 200),
    "recover": ("Восстановить доступ и заменить пароль", "recovery", "Авторизация не нужна. Передайте логин, НОВЫЙ пароль в `password` и действующий `recoveryCode`. Старые сессии отзываются, recovery-код меняется. Ответ содержит новую сессию и новый код: сохраните их. При последующем входе используйте новый пароль.", (400, 401, 409, 422, 429), 200),
    "logout": ("Выйти из текущей или всех сессий", "sessions", "Нужен действующий access-токен или гостевой token в Authorize. Без JSON-тела или с `allSessions: false` отзывается текущая сессия; с `true` — все сессии аккаунта. Ответ `204` без JSON. Удалите сохранённые токены на клиенте; Swagger Authorize очищается вручную.", (401, 422), 204),
    "me": ("Посмотреть текущий аккаунт, роли и права", "sessions", "Нужен access-токен или гостевой token в Authorize. JSON-тело не требуется. Возвращает UUID, признак гостя, роли и эффективные разрешения. У обычного нового аккаунта списки ролей и прав пустые. Изменения ролей видны без повторного входа.", (401,), 200),
    "roles": ("Получить роли и входящие в них разрешения", "roles", "JSON-тело не нужно. Используйте `id` роли из ответа для её изменения или назначения аккаунту.", (401, 403), 200),
    "create_role": ("Создать роль из существующих разрешений", "roles", "Сначала получите коды через GET `/v1/admin/permissions`. В `permissions` передаются коды, а не числовые ID. Сохраните `id` созданной роли. Создание роли не назначает её аккаунту; для назначения используйте PUT `/v1/admin/accounts/{account_id}/roles/{role_id}`. Нельзя включать права, которых у вас нет.", (400, 401, 403, 404, 409, 422), 201),
    "update_role": ("Полностью заменить настройки роли", "roles", "PUT заменяет описание, ВЕСЬ список `permissions` и флаг назначения. Исключённые права перестают действовать у владельцев роли. `isAssignable: false` запрещает новые назначения, но не отзывает существующие. Код роли изменить нельзя. Защищённые встроенные роли редактировать нельзя.", (400, 401, 403, 404, 409, 422), 200),
    "permissions": ("Получить каталог доступных разрешений", "permissions", "JSON-тело не нужно. Значения `code` используются при создании и изменении роли. Права этого аккаунта можно проверить через `/v1/auth/me`.", (401, 403), 200),
    "create_permission": ("Зарегистрировать новый код разрешения", "permissions", "Добавляет код в каталог, но не создаёт HTTP-метод или автоматическую проверку в другом сервисе. Для использования добавьте право в роль и реализуйте его проверку в нужной операции. Встроенные роли с политикой всех разрешений получают новый код автоматически.", (400, 401, 403, 409, 422), 201),
    "grant_role": ("Назначить роль зарегистрированному аккаунту", "roles", "JSON-тело не нужно: UUID аккаунта и числовой ID роли указываются в пути. Гостю роль назначить нельзя. Повторное назначение безопасно: `changed: false` означает, что активное назначение уже было. Нельзя выдавать роль с правами шире собственных.", (400, 401, 403, 404, 409, 422), 200),
    "revoke_role": ("Отозвать назначение роли у аккаунта", "roles", "JSON-тело не нужно. Отзывает назначение, сохраняя его историю; саму роль не удаляет. Если назначения уже нет, возвращает `changed: false`. Нельзя нарушить минимальное число активных владельцев защищённой роли, например отозвать последнего ADMIN.", (400, 401, 403, 404, 409, 422), 200),
    "audit": ("Посмотреть историю административных действий", "audit", "JSON-тело не нужно. Записи отсортированы от новых к старым; `limit` задаёт размер страницы, `offset` — число пропускаемых записей. Содержит успешные действия и отказы, без паролей и токенов.", (400, 401, 403, 422), 200),
}
PERMISSION_KEYS = {
    "roles": "roles_read", "create_role": "roles_create", "update_role": "roles_update",
    "permissions": "permissions_read", "create_permission": "permissions_manage",
    "grant_role": "roles_assign", "revoke_role": "roles_revoke", "audit": "audit_read",
}
SPECIFIC_ERRORS = {
    "register": {409: (LoginUnavailable, AccountAlreadyRegistered)},
    "create_role": {403: (DelegationDenied,)},
    "update_role": {403: (DelegationDenied,)},
    "grant_role": {403: (DelegationDenied,), 409: (RoleNotAssignable,)},
}
ERROR_EXAMPLES = {
    400: ("InvalidInput", "Request parameters are invalid", "Проверьте значения запроса и политику пароля."),
    401: ("InvalidCredentials", "Invalid or expired credential", "Токен или данные входа неверны, отозваны либо истекли."),
    403: ("PermissionDenied", "Required permission or role is missing", "Аккаунт определён, но требуемого права нет."),
    404: ("ResourceNotFound", "Requested resource was not found", "Аккаунт, роль или код разрешения не найдены."),
    409: ("ResourceConflict", "Operation conflicts with the current state", "Конфликт: например, запись уже существует или защищённую роль нельзя изменить."),
    422: ("InvalidRequest", "Request validation failed", "JSON, типы или обязательные поля не соответствуют схеме."),
    429: ("RateLimitExceeded", "Too many authentication attempts; retry later", "Лимит попыток превышен. Подождите Retry-After секунд."),
    500: ("InternalError", "An internal error occurred", "Внутренняя ошибка. Сохраните requestId для диагностики."),
    503: ("DatabaseUnavailable", "Auth database is temporarily unavailable", "Временная недоступность БД или неготовая схема; проверьте готовность сервиса."),
}


def endpoint_docs(name: str) -> dict:
    summary, tag, description, errors, success = ENDPOINTS[name]
    if name in PERMISSION_KEYS:
        permission = PERMISSION_MAP[PERMISSION_KEYS[name]].code
        description = f"**Авторизация:** Bearer accessToken. **Нужно разрешение:** `{permission}`.\n\n{description}"
    responses = {}
    for status in (*errors, 500, 503):
        responses[status] = deepcopy(ERROR_RESPONSES[status])
        code, detail, hint = ERROR_EXAMPLES[status]
        value = {"code": code, "detail": detail, "requestId": REQUEST_ID, "errors": []}
        if status == 422:
            value["errors"] = [{"field": "body.login", "type": "missing", "message": "Required field is missing"}]
        responses[status]["description"] = hint
        responses[status]["content"] = {"application/json": {"examples": {"error": {"summary": code, "value": value}}}}
        for error in SPECIFIC_ERRORS.get(name, {}).get(status, ()):
            responses[status]["content"]["application/json"]["examples"][error.code] = {
                "summary": error.code,
                "value": {"code": error.code, "detail": error.public_detail, "requestId": REQUEST_ID, "errors": []},
            }
        responses[status]["headers"]["X-Request-ID"]["description"] = "UUID запроса; совпадает с requestId в JSON ошибки."
        if "Retry-After" in responses[status]["headers"]:
            responses[status]["headers"]["Retry-After"].update(description="Время до повтора в секундах.", example="1" if status == 503 else "300")
        if "WWW-Authenticate" in responses[status]["headers"]:
            responses[status]["headers"]["WWW-Authenticate"].update(description="Ожидается Bearer-токен.", example="Bearer")
    responses[success] = {"description": "Выход выполнен. Тело ответа отсутствует." if success == 204 else "Операция выполнена успешно."}
    if success != 204:
        responses[success]["content"] = {"application/json": {"examples": SUCCESS_EXAMPLES[name]}}
    return {"summary": summary, "description": description, "tags": [TAGS[tag]], "responses": responses}
