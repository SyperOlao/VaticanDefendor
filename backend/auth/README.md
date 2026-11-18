# Auth

Микросервис аккаунтов: гостевой вход, регистрация, вход по паролю, обновление токенов, выход и восстановление доступа через recovery-код. Управляет ролями, разрешениями и аудитом административных действий. Использует отдельную PostgreSQL-базу.

Для rating доступна внутренняя gRPC-проверка токена `vatican.auth.v1.AuthService/Authenticate`.
В главном Compose слушатель включён на `auth:50051`, без публикации порта на хост.
Он использует те же правила аккаунтов/сессий, что `/v1/auth/me`, и дополнительно требует
сервисный секрет в metadata. [Контракт, настройки, TLS и восстановление](../docs/rating/GRPC.ru.md).

## Запуск через Docker Compose

Нужны Docker и Docker Compose. Из корня репозитория:

```sh
cd backend/infra
cp .env.example .env
```

Укажите разные пароли `VD_AUTH_DB_PASSWORD`, `VD_POSTGRES_PASSWORD`, `VD_CLICKHOUSE_PASSWORD` в `.env`, затем:

```sh
docker compose up -d --build
docker compose ps
```

Общий Compose создаст отдельные PostgreSQL для auth и игровых данных, ClickHouse для аналитики, применит миграции auth и запустит auth. Подключения к БД описаны в [README инфраструктуры](../infra/README.md). По умолчанию:

- Swagger: http://127.0.0.1:8081/docs
- Проверка процесса: http://127.0.0.1:8081/health/live
- Готовность сервиса и схемы: http://127.0.0.1:8081/health/ready

Внешний порт меняется через `VD_AUTH_HOST_PORT` в [настройках Compose](../infra/.env.example).

Команды из `backend/infra/`:

```sh
# Логи сервиса.
docker compose logs -f auth

# Пересобрать и применить изменения кода или конфигурации.
docker compose up -d --build

# Повторно применить миграции.
docker compose run --rm auth_migrate

# Посмотреть текущую ревизию схемы.
docker compose exec auth python -m alembic current

# Остановить сервисы с сохранением базы.
docker compose down
```

## Локальный запуск — bash/zsh

Нужны Python 3.12 и PostgreSQL. Заранее создайте пользователя и базу для auth; их параметры указываются в `.env`.

Из корня репозитория:

```sh
cd backend/auth
python3.12 -m venv .venv
. .venv/bin/activate
python -m pip install -r requirements.txt
cp .env.example .env
```

Заполните [`.env`](.env.example): задайте `VD_AUTH_DATABASE_URL` либо отдельные поля `VD_AUTH_DB_HOST`, `VD_AUTH_DB_PORT`, `VD_AUTH_DB_NAME`, `VD_AUTH_DB_USER`, `VD_AUTH_DB_PASSWORD`. Непустой URL имеет приоритет. В отдельном поле пароль можно указывать без percent-encoding.

Сервис сам не загружает `.env`; перед командами экспортируйте его в текущую оболочку:

```sh
set -a
. ./.env
set +a

python -m vatican_auth.migrate
python -m vatican_auth
```

Локальный адрес по умолчанию: http://127.0.0.1:8080/docs. Порт задаётся через `VD_AUTH_PORT`. После изменения `.env` повторите экспорт и перезапустите процесс.

## Миграции

Команды выполняются из `backend/auth/` с активным виртуальным окружением и экспортированным `.env`.

```sh
# Применить все миграции.
python -m vatican_auth.migrate

# Текущая ревизия и история.
python -m alembic current
python -m alembic history

# Проверить, совпадают ли таблицы БД с ORM-моделями.
python -m alembic check

# Создать миграцию после изменения моделей; проверьте созданный файл перед применением.
python -m alembic revision --autogenerate -m "describe_schema_change"
python -m vatican_auth.migrate

# Откатить последнюю миграцию. Откат начальной ревизии удаляет таблицы auth.
python -m alembic downgrade -1
```

Миграции находятся в `alembic/versions/`. HTTP-сервер самостоятельно их не применяет; в Compose это делает `auth_migrate` перед запуском API.

## Первый администратор

Сначала зарегистрируйте обычный аккаунт через `POST /v1/auth/register` в Swagger. При регистрации сохраните выданный recovery-код. Автоматически административные права не выдаются.

Для Docker, из `backend/infra/`:

```sh
docker compose exec auth python -m vatican_auth.cli.bootstrap_admin --login ваш_логин
```

Для локального запуска, из `backend/auth/` с активным окружением и загруженным `.env`:

```sh
python -m vatican_auth.cli.bootstrap_admin --login ваш_логин
```

Команда выдаёт встроенную роль ADMIN существующему зарегистрированному аккаунту. Повторный запуск не дублирует активное назначение. После этого в Swagger можно авторизоваться access-токеном и использовать `/v1/admin/*` для управления ролями и разрешениями.

## Тесты

Из `backend/auth/`, в активном виртуальном окружении:

```sh
python -m pip install -r requirements-dev.txt
```

Укажите `VD_TEST_AUTH_DATABASE_URL` в `.env`: это должна быть отдельная существующая PostgreSQL-база, где тестовому пользователю разрешено создавать схемы. Повторите экспорт `.env` и запустите:

```sh
PYTHONPATH=. python -m pytest -q tests
```

Каждый тест БД создаёт и удаляет свою временную схему. Если `VD_TEST_AUTH_DATABASE_URL` пустой, тесты, которым нужна БД, пропускаются.
