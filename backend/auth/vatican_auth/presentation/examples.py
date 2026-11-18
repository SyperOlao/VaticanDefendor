"""Fictional OpenAPI examples, shared by request bodies and response schemas."""
from ..application.catalog import AUDIT_ACTION_MAP, AUDIT_RESULT_MAP, PERMISSION_MAP, ROLE_MAP
from ..migrate import SCHEMA_REVISION

ACCOUNT_ID = "8be01f80-0f28-4b43-b718-05c4cd96d9a2"
SESSION_ID = "f567fc37-f14f-40a3-b8f4-24b5bac603e3"
REQUEST_ID = "a8c9596f-6ac4-4214-97b4-c2f432065aad"
ACCESS_TOKEN = "a" * 43
REFRESH_TOKEN = "r" * 43
RECOVERY_CODE = "c" * 43
GUEST_TOKEN = "g" * 43
LOGIN = {"login": "demo_player", "password": "ExamplePass123!"}

GUEST = {"accountId": ACCOUNT_ID, "token": GUEST_TOKEN, "expiresAt": "2030-01-31T12:00:00Z"}
SESSION = {
    "accountId": ACCOUNT_ID, "sessionId": SESSION_ID,
    "accessToken": ACCESS_TOKEN, "refreshToken": REFRESH_TOKEN,
    "accessExpiresAt": "2030-01-01T12:15:00Z", "refreshExpiresAt": "2030-01-31T12:00:00Z",
    "tokenType": "Bearer",
}
CREDENTIAL = {**SESSION, "recoveryCode": RECOVERY_CODE}
ACCESS = {"accountId": ACCOUNT_ID, "sessionId": SESSION_ID, "isGuest": False, "roles": [], "permissions": []}
ADMIN_ACCESS = {**ACCESS, "roles": [ROLE_MAP["admin"].code], "permissions": sorted(p.code for p in PERMISSION_MAP.values())}
ROLE_CREATE = {
    "code": "MODERATOR", "description": "Просмотр ролей и разрешений",
    "permissions": [PERMISSION_MAP["roles_read"].code, PERMISSION_MAP["permissions_read"].code],
    "isAssignable": True,
}
ROLE = {**ROLE_CREATE, "id": 2}
ROLE_UPDATE = {key: value for key, value in ROLE_CREATE.items() if key != "code"}
PERMISSION_CREATE = {"code": "shop.products.write", "description": "Управление товарами магазина"}
PERMISSION = {**PERMISSION_CREATE, "id": 9}
AUDIT = {
    "id": "d0d4d923-a8db-47ac-b366-cf3ec07931a0", "actorAccountId": ACCOUNT_ID,
    "targetAccountId": ACCOUNT_ID, "action": AUDIT_ACTION_MAP["roles_assign"],
    "result": AUDIT_RESULT_MAP["succeeded"], "details": {"role_id": 2, "changed": True},
    "createdAt": "2030-01-01T12:00:00Z",
}


def example(summary, value, description=None):
    result = {"summary": summary, "value": value}
    if description:
        result["description"] = description
    return result


REQUEST_EXAMPLES = {
    "register": {
        "new_account": example("Новый аккаунт", LOGIN, "Создать UUID, логин и пароль. Вернутся токены и recovery-код."),
        "upgrade_guest": example("Привязать логин к гостю", {**LOGIN, "guestToken": GUEST_TOKEN},
                                 "Замените guestToken токеном из POST /v1/guests. UUID гостя сохранится."),
    },
    "login": {"password": example("Вход по логину и паролю", LOGIN)},
    "refresh": {"rotate": example("Получить новую пару токенов", {"refreshToken": REFRESH_TOKEN},
                                  "Подставьте текущий refreshToken. Использовать его повторно нельзя.")},
    "recover": {"reset": example("Заменить пароль через recovery-код", {
        "login": LOGIN["login"], "password": "NewExamplePass123!", "recoveryCode": RECOVERY_CODE,
    })},
    "logout": {
        "current_session": example("Выйти из текущей сессии", {"allSessions": False}),
        "all_sessions": example("Выйти из всех сессий аккаунта", {"allSessions": True}),
    },
    "create_role": {"moderator": example("Роль с ограниченным набором прав", ROLE_CREATE)},
    "update_role": {
        "replace": example("Заменить описание и полный набор прав", ROLE_UPDATE),
        "disable_assignments": example("Запретить новые назначения роли", {**ROLE_UPDATE, "isAssignable": False},
                                       "Существующие назначения остаются активными. Для их отмены используйте DELETE назначения."),
    },
    "create_permission": {"shop_permission": example("Добавить код разрешения", PERMISSION_CREATE,
        "Создаёт запись в каталоге прав. Проверку этого кода в нужном сервисе реализуют отдельно.")},
}

SUCCESS_EXAMPLES = {
    "live": {"live": example("Процесс работает", {"status": "live"})},
    "ready": {"ready": example("База доступна, миграции актуальны", {"status": "ready", "schemaRevision": SCHEMA_REVISION})},
    "create_guest": {"guest": example("Гостевой аккаунт", GUEST)},
    "register": {"registered": example("Регистрация завершена", CREDENTIAL)},
    "login": {"session": example("Новая сессия", SESSION)},
    "refresh": {"session": example("Новая пара токенов", SESSION)},
    "recover": {"recovered": example("Новый пароль, сессия и recovery-код", CREDENTIAL)},
    "me": {
        "player": example("Зарегистрированный игрок без ролей", ACCESS),
        "admin": example("Администратор", ADMIN_ACCESS),
    },
    "roles": {"roles": example("Доступные роли", [ROLE])},
    "create_role": {"role": example("Созданная роль", ROLE)},
    "update_role": {"role": example("Обновлённая роль", ROLE)},
    "permissions": {"permissions": example("Каталог разрешений", [PERMISSION])},
    "create_permission": {"permission": example("Новое разрешение", PERMISSION)},
    "grant_role": {
        "granted": example("Роль назначена", {"changed": True}),
        "already_granted": example("Активное назначение уже существовало", {"changed": False}),
    },
    "revoke_role": {
        "revoked": example("Назначение отозвано", {"changed": True}),
        "already_revoked": example("Активного назначения не было", {"changed": False}),
    },
    "audit": {"entries": example("Аудит от новых записей к старым", [AUDIT])},
}
