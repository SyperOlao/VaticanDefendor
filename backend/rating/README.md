# Рейтинг VaticanDefendor

Рейтинговые доски, сумма XP за доставленные забеги и журнал однократных начислений. Позиции вычисляются при чтении: score по убыванию, UUID игрока для равных очков.

Из `backend/rating`:

```sh
python -m pip install -r requirements-dev.txt
# Задайте VD_RATING_DATABASE_URL, VD_SERVICE_SECRET и VD_AUTH_GRPC_TARGET (см. .env.example).
python -m vatican_rating.migrate
python -m vatican_rating
```

Требуется Python 3.12. Общий Compose запускает API на http://127.0.0.1:28085/docs.

Swagger `/docs` содержит инструкцию на русском, описания всех полей, примеры запросов и ответов
и единый формат ошибок. Если порт переопределён в Compose, используйте свой `VD_RATING_HOST_PORT`.
В **Authorize → HTTPBearer** вставьте только гостевой `token` или `accessToken` сервиса auth
без слова `Bearer`. Для внутренних методов отдельно используется **Authorize → ServiceSecret**
со значением `VD_SERVICE_SECRET`. В **Try it out** можно выбрать пример начисления 80 или 0 XP;
повтор с теми же идентификаторами не удваивает очки. OpenAPI JSON доступен по `/openapi.json`,
альтернативное представление — `/redoc`.

| Метод | Доступ | Результат |
| --- | --- | --- |
| `GET /v1/leaderboards?limit=50&offset=0` | Публичный | Доски `{code, scoreSource}` по возрастанию code, включая пустые |
| `GET /v1/leaderboards/{code}?limit=50&offset=0` | Публичный | Страница игроков `{accountId, score, position}` |
| `GET /v1/leaderboards/{code}/me` | Bearer opaque-токен auth | Позиция текущего игрока на доске |
| `POST /internal/messages` | `X-Service-Secret` | Идемпотентное начисление за забег |
| `GET /internal/messages/{message_id}` | `X-Service-Secret` | Состояние AMQP inbox и публикации результата; HTTP-начисления сюда не попадают |

Пагинация: limit 1..100, offset 0..100000. Позиции начинаются с 1; при равных очках меньший UUID идёт раньше, позиции уникальны. `/me` возвращает `{"board":"run-xp","entry":null}`, если игрок ещё не участвовал, и 404 для неизвестной доски. Нулевые очки — полноценная запись с позицией. Собственная позиция доступна и за пределами лимита offset.

Владелец `/me` определяется через внутренний gRPC `vatican.auth.v1.AuthService/Authenticate`;
гостевые токены тоже поддерживаются. Используются те же правила auth, что у публичного `/v1/auth/me`.
Auth вызывается до открытия транзакции rating. Канал переиспользуется, общий лимит вызова — 2 секунды,
до 3 попыток только при `UNAVAILABLE`; HTTP fallback отсутствует. При недоступности auth `/me` возвращает
503, при неверном/отозванном токене — 401. Публичные методы и RabbitMQ-начисления не обращаются к auth.
Compose запускает rating после его миграций без ожидания готовности auth. Чтение не создаёт игрока или
запись с нулевыми очками. Клиент не может выбрать чужой accountId для `/me`.

[Контракт gRPC, настройки, ошибки и проверки восстановления](../docs/rating/GRPC.ru.md).

Единственная начальная миграция `0001_rating_initial` создаёт всю схему: доски, очки,
квитанции уникальных забегов, дедупликацию и RabbitMQ inbox/outbox. Сразу создаётся доска
`run-xp` с источником RUN_XP. История старых миграций удалена; для прежней локальной схемы
нужен сброс БД rating и повторный запуск `rating_migrate`, а не `alembic stamp`.
Для другой доски:

```sh
python -m vatican_rating.cli.configure_board --code season-2
```

Публикация идемпотентна; источник существующей доски не меняется. Выберите код в VD_RATING_BOARD_CODE игрового сервиса. Удаление/пересоздание доски не является способом безопасного повтора начисления.

Контракт `run.rating`, schemaVersion 1:

```json
{"messageId":"UUID","eventName":"run.rating","schemaVersion":1,"payload":{
  "runId":"UUID","accountId":"UUID","boardCode":"run-xp",
  "rulesetId":"xp-v1","contentReleaseId":"sha256-release","points":80}}
```

Очки — целое 0..2^63−1. Они рассчитываются game_backend из утверждённых правил XP; клиентского метода изменения счёта нет. Rating не читает игровые таблицы. Новый runId добавляет points к доске; доставленные не по порядку забеги также суммируются. Нулевое начисление фиксируется и не может быть заменено положительным при повторе.

Счёт, source receipt и consumed message сохраняются в одной локальной транзакции. Ответ `APPLIED` содержит исходный payload плюс `boardId` и `scoreAfter` в receipt. Один runId учитывается только один раз, даже при новом messageId. Изменение владельца, доски, points, rulesetId или contentReleaseId даёт 409 без изменений. Повтор возвращает сохранённый исторический scoreAfter; текущий счёт смотрите в leaderboard API. Переполнение не обрезается и не оставляет частично сохранённую операцию.

Неизвестная доска/несовместимый score_source возвращает 422, game_backend сохраняет REVIEW. После настройки доски повторите исходное сообщение через игровой CLI replay_rewards. Сбой сервиса возвращается в обычные автоматические повторы game worker; общий distributed transaction не требуется. Миграция не пересчитывает старые XP и не обнуляет импортированные очки.

Временные ошибки БД (deadlock, serialization failure, ожидание блокировки и таймаут SQL) возвращают 503 с `Retry-After: 1`; worker повторяет исходное сообщение автоматически. Конфликт сохранённого payload и переполнение счёта остаются 409. Отказ транзакции откатывает очки, receipt и дедупликацию вместе. Начисление использует атомарное сложение в PostgreSQL, чтобы сохранить изменение, совершённое другим процессом во время ожидания строки.

API по умолчанию использует `VD_RATING_DB_POOL_SIZE=10`, `VD_RATING_DB_LOCK_TIMEOUT_MS=1000` и `VD_RATING_DB_STATEMENT_TIMEOUT_MS=5000`. Ожидание свободного соединения — до 3 секунд; простаивающая транзакция закрывается PostgreSQL через 15 секунд (либо два statement timeout, если это больше). Эти лимиты не задаются команде миграций. Неверные лимиты и неподходящий сервисный секрет отклоняются при запуске.

Внутренние сообщения ограничены 16 KiB, включая потоковую передачу без достоверного Content-Length. Секрет проверяется до чтения JSON; превышение размера даёт 413, незавершённая загрузка после 5 секунд — 408. При отказе HTTP/1-соединение загрузки закрывается; game worker автоматически повторяет 408 с тем же messageId. `/health/live` не обращается к БД; `/health/ready` требует ровно одну ожидаемую ревизию миграций.

Тесты с отдельной PostgreSQL:

```sh
VD_TEST_DATABASE_URL='postgresql://user:password@localhost:5432/vatican_test' python -m pytest -q tests
```

RabbitMQ включён в общем Compose через `VD_RATING_TRANSPORT=rabbitmq`. Текущие бинарники требуют rating `0001_rating_initial` и game_backend `0001_initial` для обоих транспортов. `game_rewards` публикует команды, `rating_worker` начисляет очки и отправляет результат, `game_rating_results` сохраняет подтверждение. Publisher confirm сохраняет PENDING; COMPLETED появляется после проверенного APPLIED.

```sh
# Из backend/rating, после настройки VD_RATING_RABBITMQ_URL и БД
python -m vatican_rating.worker
# Из backend/infra, после настройки .env
docker compose up -d --build rabbitmq rating_worker game_rewards game_rating_results
```

[Топология, настройки, гарантии доставки, восстановление и live-тесты](../docs/rating/RABBITMQ.ru.md).

Нагрузка с параллельной HTTP/AMQP-доставкой, повторными messageId/runId и аварийными перезапусками:

```sh
# Из корня репозитория; Python 3.12+, зависимости rating/requirements-dev.txt и Docker.
python backend/rating/scripts/load_check.py --runs 2500 --clients 64 \
  --report backend/docs/rating/verification/load-result.json
```

Скрипт сам создаёт отдельные PostgreSQL и RabbitMQ со случайными паролями, запускает два API,
четыре rating worker, отправителей/получателей game_backend и после проверки удаляет тестовые
контейнеры с данными. Для SIGKILL проверяется метка владения контейнером; рабочий Compose не используется.
Обычный pytest пропускает этот длительный тест. В CI запускается отдельный профиль 256 забегов / 16 клиентов.
Проверяются суммы по каждому игроку/доске, неизменяемость квитанций, все подтверждения game_backend,
отсутствие незавершённых команд, карантина и потерянных результатов. При падении worker незавершённая
аренда команды восстанавливается естественным образом после 60 секунд; тест не ускоряет её SQL-правками.

[Результаты нагрузочных прогонов 17.11.2026](../docs/rating/verification/LOAD_CHECK.ru.md).
