# Analytics VaticanDefendor

Приём подтверждённых забегов, подарков, кейсов и покупок shop, неизменяемый inbox и запись событий/фактов в ClickHouse. Сервис имеет собственные PostgreSQL Base и миграции для технического inbox; игровые таблицы он не читает. В ClickHouse — 28 прикладных таблиц, в PostgreSQL — 2 технические таблицы. Metadata баз разделены.

Из `backend/analytics`:

```sh
python -m pip install -r requirements-dev.txt
# Задайте VD_ANALYTICS_INBOX_DATABASE_URL (PostgreSQL),
# VD_ANALYTICS_DATABASE_URL (clickhouse+http://...) и VD_SERVICE_SECRET.
python -m vatican_analytics.migrate
python -m vatican_analytics.init_db
python -m vatican_analytics
# В другом процессе:
python -m vatican_analytics.worker
```

`init_db` создаёт отсутствующие ClickHouse-таблицы через ORM, включая fact_run_settlements, fact_case_settlements и fact_shop_purchases, сохраняя прежние таблицы и данные. Для LOCAL он повторяемо добавляет simulation_mode LowCardinality(String) DEFAULT 'DEDICATED' в существующую fact_run_settlements; другие существующие поля не меняются, нужны права ALTER. PostgreSQL inbox обновляется Alembic, текущая ревизия `0001_analytics_inbox`. Пример переменных — `.env.example`.

Общий Compose содержит analytics_db, analytics_migrate, analytics_schema, analytics и analytics_worker. Добавьте отдельный VD_ANALYTICS_INBOX_DB_PASSWORD в окружение; существующий ClickHouse использует VD_CLICKHOUSE_PASSWORD. API доступен на http://127.0.0.1:8086/docs. Готовность API проверяет PostgreSQL inbox: при сбое ClickHouse можно продолжать надёжно принимать сообщения.

Внутренний `POST /internal/messages` требует X-Service-Secret, общий с game_backend. Для `eventName=run.analytics`, schemaVersion=1, payload содержит accountId/runId, rulesetId/contentReleaseId, completedAt в UTC, outcome, churchLevel, stats и сокращённый progression. Необязательный simulationMode принимает LOCAL или DEDICATED; отсутствие поля означает исторический DEDICATED. Неизвестные поля, в том числе токены, логины, ник и произвольные пользовательские данные, отклоняются до записи. Клиентского telemetry API нет.

HTTP 202/PENDING означает, что неизменяемое сообщение сохранено, но факты ещё не записаны полностью. HTTP 200/APPLIED с receipt возвращается после завершения worker. Receipt содержит runId, accountId, SHA-256 versioned payload и стабильные eventIds. Game worker считает 202 временным состоянием и повторяет доставку. HTTP 409/REVIEW требует исправления причины и повторного запуска. Статус источника без исходного payload: защищённый `GET /internal/runs/{run_id}`.

Worker создаёт raw_game_events `run.completed`, `xp.granted` (если есть закреплённая прогрессия), `player.level_up` (при повышении уровня), fact_run_settlements, fact_xp_transactions и fact_player_level_ups. XP за отдельный забег сохраняется отдельно от totalXp. Нулевая выдача XP тоже является событием. При пропуске нескольких уровней фиксируется один переход с old_level/new_level.

fact_run_settlements содержит только известную серверу сводку. Недоступные длительность симуляции, урон, карта, telemetry session и прочие поля прежней fact_runs не заменяются вымышленными нулями. Денежные факты не создаются из обещанной награды забега: их должен подтверждать владелец wallet. Суммы счётчиков хранятся UInt64; если они не помещаются в старые nullable UInt32-поля факта XP, эти поля остаются NULL, а полные значения доступны в raw event и fact_run_settlements.

Защита повторов:

- Пара `(eventName, sourceId)` задаёт единственный неизменяемый источник; для run.analytics sourceId — runId; повтор с новым messageId возвращает прежний результат. Изменение владельца или payload даёт 409.
- Inbox и привязка messageId сохраняются в одной транзакции PostgreSQL. Проекция ClickHouse выполняется без удержания PostgreSQL-соединения.
- При частичной записи, потере ответа или сбое сохранения подтверждения worker повторяет тот же snapshot и те же ключи. ReplacingMergeTree может физически хранить повторы до слияния: **факты и события нужно читать с FINAL перед count/SUM** либо с эквивалентной дедупликацией. Инкрементальные агрегаты поверх сырых повторов не создаются.
- Аренды защищают от устаревших подтверждений; после 20 попыток source переходит в REVIEW. Источники, alias и завершённые receipts не удаляются при повторном запуске.

После устранения причины:

```sh
python -m vatican_analytics.cli.replay_events <run_uuid>
```

Если game_backend тоже достиг REVIEW, после восстановления/повторной проекции выполните игровой `python -m vatican_backend.cli.replay_rewards <analytics_message_uuid>`. Оба повтора используют исходные сообщения. При обычной кратковременной недоступности повторы автоматические.

Исторические завершённые операции не пересылаются автоматически. Внешние платежи и произвольная клиентская телеметрия не принимаются этим контрактом.

Подарки и кейсы:

- `level-gift.analytics` v1: claimId, accountId, rulesetId, rewardedLevel, rewardBundleId, status=CLAIMED, claimedAt (UTC). Создаёт raw event `level_gift.claimed` и fact_level_gift_claims.
- `case.analytics` v1: openingId, accountId, caseId, caseCode, rulesetId, paymentMode, rewardBundleId, rarity, pityBefore/pityAfter, guaranteedTriggered, entryIndex, status=COMPLETED, completedAt (UTC). Создаёт raw event `case.opened` и новую fact_case_settlements.

Эти события описывают полностью подтверждённую выдачу. Отказ оплаты и частичная выдача не считаются успешным открытием/получением. Время — момент завершения операции. Неподтверждённая информация о duplicate item не выдумывается: прежняя fact_case_openings остаётся без изменения, новая fact_case_settlements содержит только известные показатели. В payload нет seed, произвольных receipts, балансов, токенов или ника.

Миграция `0001_analytics_inbox` сохраняет прежние PENDING/COMPLETED/REVIEW источники, leases, hashes, aliases и receipts забегов, переименовывая run_id в source_id и добавляя event_name к ключу inbox. Один UUID может независимо обозначать забег, подарок и кейс; messageId остаётся глобально неизменяемым. Старый контракт run.analytics v1, receipts и GET /internal/runs/{runId} сохраняются. Общий защищённый статус: GET /internal/sources/{kind}/{sourceId}, где kind — run, level-gift или case.

При обновлении остановите прежние analytics API/worker, примените migrate и init_db, затем запустите обновлённые процессы. init_db добавляет fact_case_settlements без изменения старых ClickHouse-таблиц и данных. Обновление game_backend/game_rewards выполняйте после analytics; игровая схема не меняется.

Повтор REVIEW-источников:

```sh
python -m vatican_analytics.cli.replay_events <claim_uuid> --event-name level-gift.analytics
python -m vatican_analytics.cli.replay_events <opening_uuid> --event-name case.analytics
```

Без --event-name команда продолжает работать с run.analytics. После REVIEW game worker также нужен его replay_rewards с соответствующим analytics.messageId. Подтверждение выдаётся только после полной проекции; чтение ClickHouse с FINAL остаётся обязательным перед count/SUM.

Подтверждённые покупки shop:

- `shop.purchase.analytics` v1: purchaseId, accountId, operationId, offerId, catalogVersion, currencyId=PRESTIGE, chargedAmount (в том числе 0), weaponConfigurationId, weaponInstanceIds (один подтверждённый UUID), status=Completed, completedAt в UTC. Лишние поля, неподтверждённый статус, неверная валюта и источник отклоняются до inbox.
- purchaseId вычисляется как UUIDv5(accountId, `shop.purchase:` + канонический operationId). Источник — пара `(shop.purchase.analytics, purchaseId)`. Сумма, товар, версия и экземпляр неизменяемы даже при новом messageId; изменённый snapshot получает 409.
- Проекция: raw_game_events `shop.purchase_confirmed` с source=shop и отдельная `fact_shop_purchases`. Покупка за виртуальную валюту не создаёт строки в прежней `fact_purchases` или выручку в денежных агрегатах. Факт содержит стабильный purchase_id, игрока/операцию, товар/версию, реально подтверждённую сумму и экземпляр оружия.

Защищённый статус: `GET /internal/sources/shop-purchase/{purchaseId}`. Только APPLIED/HTTP 200 означает полную проекцию. При частичном сбое или потере подтверждения используются прежний snapshot и ключи; для count/SUM читайте факты с FINAL. Миграция `0001_analytics_inbox` расширяет только ограничение типов inbox, сохраняя все источники, aliases, leases и receipts. Сначала обновите analytics (migrate/init_db/API/worker), затем отправителя shop; прежние ClickHouse-таблицы и данные не изменяются.

```sh
python -m vatican_analytics.cli.replay_events <purchase_uuid> --event-name shop.purchase.analytics
# После REVIEW на стороне shop:
python -m vatican_shop.cli.replay_analytics <account_uuid> <operation_uuid>
```

Повтор доставки аналитики не запускает покупку, списание или выдачу заново.

Проверка с отдельными тестовыми базами:

```sh
VD_TEST_DATABASE_URL='postgresql://user:password@localhost:5432/analytics_test' \
VD_TEST_ANALYTICS_DATABASE_URL='clickhouse+http://user:password@localhost:8123/analytics_test' \
python -m pytest -q tests --import-mode=importlib
```

PostgreSQL-тесты используют изолированные схемы. Live ClickHouse-тесты добавляют строки в указанную отдельную тестовую базу, проверяют частичные записи и повторную доставку с FINAL. Дополнительная проверка через chDB доступна при его установке; он не является зависимостью сервиса. Полная историческая сверка моделей с проектом — в `../docs/game_backend/PDF_SCHEMA_AUDIT.ru.md`.

Дневные итоги по забегам, XP, уровням, подаркам и кейсам доступны через защищённый `GET /internal/reports/daily?day=YYYY-MM-DD` и CLI `python -m vatican_analytics.cli.daily_report YYYY-MM-DD`. Это чтение актуальных фактов с FINAL без записи в `agg_*`. Контракт и запуск: [REPORTING.ru.md](../docs/analytics/REPORTING.ru.md). Текущая сверка архитектуры и незавершённые части схемы: [SCHEMA_REVIEW.ru.md](../docs/analytics/SCHEMA_REVIEW.ru.md).

Межсервисные проверки, исправления обработки HTTP 408 и ограничение нового live-прогона: [COMPATIBILITY.ru.md](../docs/analytics/COMPATIBILITY.ru.md).


LOCAL-забеги поступают только после проверки и успешной фиксации игрового начисления. Режим берётся из серверного Run, а не из произвольного клиентского поля. Raw events содержат simulationMode=LOCAL, receipt защищает режим в payloadHash; summary хранит simulation_mode, факт XP — source_type=LOCAL_RUN. Старые dedicated payload и hash остаются прежними; добавление/смена режима у существующего runId считается изменением неизменяемого источника и даёт 409. Дедупликация по (eventName, runId), leases, REVIEW и replay одинаковы для обоих режимов. Дневной отчёт включает оба режима; для раздельного анализа фильтруйте fact_run_settlements FINAL по simulation_mode.

Сначала разверните код analytics, выполните init_db и запустите обновлённые analytics API/worker, затем обновите producer game_backend и его worker. Повтор init_db сохраняет старые строки: прежняя таблица получает mode DEFAULT DEDICATED, новое поле не требует пересоздания таблицы. PostgreSQL inbox остаётся на прежних миграциях, число ClickHouse-таблиц не меняется.
# Операции кошелька

`0001_analytics_inbox` разрешает `wallet.transaction.analytics` v1. Источник — transactionId ledger wallet, независимо от messageId транспорта. Payload содержит accountId, currencyId, delta, balanceAfter, reason, sourceType, sourceId, itemIndex, occurredAt UTC; добавочные поля отклоняются. Смена владельца или содержимого существующего источника даёт 409.

Проекция записывает raw_game_events (`source=wallet`, event_name=wallet.economy_transaction) и существующий fact_economy_transactions. Читайте суммы с `FINAL` и с группировкой по валюте. Это движение игровой валюты; оно не смешивается с выручкой fact_purchases. Исторические события сохраняют время исходной ledger-строки. Статус: `/internal/sources/wallet-transaction/{transactionId}` с service credential. Для REVIEW: `python -m vatican_analytics.cli.replay_events <transactionId> --event-name wallet.transaction.analytics`.


Общий путеводитель по Swagger, доступу и примерам запросов: [../SWAGGER.ru.md](../docs/SWAGGER.ru.md).
