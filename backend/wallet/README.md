# Кошелёк VaticanDefendor

Валюты, балансы, журнал начислений и списаний. Престиж хранится только здесь.

Из `backend/wallet`:

```sh
python -m pip install -r requirements-dev.txt
# Задайте VD_WALLET_DATABASE_URL, VD_SERVICE_SECRET и VD_AUTH_GRPC_TARGET.
python -m vatican_wallet.migrate
python -m vatican_wallet
# В отдельном процессе с теми же настройками и VD_ANALYTICS_URL:
python -m vatican_wallet.worker
```

Общий Compose запускает сервис на http://127.0.0.1:28082/docs, gateway — http://127.0.0.1:28087. Клиентские методы: `GET /v1/wallet`, `GET /v1/wallet/transactions`. Они принимают Bearer-токен auth; внутренние команды защищены `X-Service-Secret`. Wallet не запрашивает игровые определения для изменения баланса.

Команды магазина `POST /internal/charges` и `POST /internal/refunds` принимают `accountId`, `operationId`, `amount`. Обработанная команда возвращает HTTP 200 со статусом `APPLIED` либо сохранённым `REJECTED` (`InsufficientFunds` / `ChargeNotFound`), с теми же идентификаторами, суммой и `currencyId=PRESTIGE`. Повтор с другой суммой возвращает 409; повтор отказа не списывает деньги после пополнения.

Тесты выполняются на PostgreSQL в изолированных схемах:

```sh
VD_TEST_DATABASE_URL='postgresql://user:password@localhost:5432/vatican_test' python -m pytest -q tests
```

Подарки уровней принимаются через защищённый `POST /internal/messages` с eventName `level.reward` и schemaVersion 1. Поддерживаются PRESTIGE и GEMS. `claimId` + `itemIndex` определяют неизменяемую строку выдачи; receipt хранится вместе с начислением и дедупликацией. `GET /v1/wallet` сохраняет поля престижа и дополнительно возвращает массив `balances`.

Кейсы требуют миграцию `0001_inventory_initial`. Внутренний `case.payment` v1 принимает `accountId`, `openingId`, `caseCode`, `currencyId` (PRESTIGE/GEMS), `amount`. Внешний статус ответа APPLIED означает обработку сообщения; `receipt.outcome` — APPLIED или REJECTED/InsufficientFunds. Отказ тоже сохраняется и не превращается в списание после пополнения при повторе. `case.reward` v1 начисляет валюту по `openingId`/`itemIndex` с теми же полями rewardType/ref/quantity, что у level.reward. Списание/начисление и source receipt сохраняются в одной транзакции; новый messageId не повторяет операцию, изменение владельца или payload даёт 409.

## Награды забега и история операций

`run.currency_reward` v1 принимает только `accountId`, `runId`, `amount` (положительный int64). Один `runId` имеет одного владельца и одну сумму PRESTIGE. Изменение владельца/суммы даёт 409 даже с новым `messageId`. Ответ содержит `messageId`, `status=APPLIED`, `receipt` с исходными accountId/runId/amount/currencyId/balanceAfter. Повтор после последующих трат возвращает исходный receipt. Запись баланса, ledger, source receipt и inbox коммитятся атомарно. Все consumers кошелька используют общую блокировку messageId перед блокировками источника/аккаунта. HTTP-модели отклоняют bool, дроби и строки вместо суммы или schemaVersion.

Для создания схемы примените `python -m vatican_wallet.migrate`, затем запустите обновлённый API. Она переносит прежние RUN_REWARD из ledger в `run_reward_receipts`, добавляет уникальность источника забега и индекс истории. При противоречивых исторических выдачах обновление откатывается: требуется явное согласование данных, деньги автоматически не списываются. При переносе старой БД в уже обновлённую wallet-схему consumer принимает существующее начисление из ledger без повторной выдачи. Работающие базы и контейнеры этой задачей не обновлялись.

`GET /v1/wallet/transactions?currencyId=PRESTIGE&limit=50&before=<transactionId>` возвращает `transactions` и `nextCursor`. currencyId и before необязательны, limit — 1–100. Каждая строка содержит transactionId, currencyId, delta, balanceAfter, reason, sourceType, sourceId, itemIndex, createdAt (UTC). Порядок: createdAt DESC, transactionId DESC; UUID разрешает одинаковые времена, но не обозначает причинный порядок. Для следующей страницы передайте nextCursor в before с прежним фильтром валюты. Чужой/отсутствующий курсор либо курсор другой валюты даёт одинаковый 422. Аккаунт берётся только из auth. Чтение истории не создаёт кошелёк и не начисляет стартовый баланс.

Пока `GET /v1/wallet` при первом обращении сохраняет прежнее начисление 500 PRESTIGE. Эта сумма не подтверждена проектным PDF и требует отдельного продуктового решения; она не менялась в рамках усиления идемпотентности. GEMS появляется в balances после первой операции этой валюты. Реальные платежи и возвраты IAP требуют отдельных сценариев.

## Доставка операций в аналитику

Миграция wallet `0001_wallet_initial` добавляет lease/REVIEW/receipt в существующий outbox и ставит все исторические строки ledger в очередь. Балансы и ledger при этом не меняются. Каждая новая операция, включая стартовое начисление, награды, оплату кейса/прокачки, покупку и возврат, сохраняет свой outbox в той же транзакции. Отклонённые команды и идемпотентные повторы новых финансовых событий не создают.

`wallet_worker` отправляет HTTP `wallet.transaction.analytics` v1 в `VD_ANALYTICS_URL` (в Compose `http://analytics:8080`). Идентификаторы сообщения и события стабильны и выводятся из transactionId. В payload: transactionId, accountId, currencyId, delta, balanceAfter, reason, sourceType, sourceId, itemIndex, occurredAt UTC. Внешних вызовов внутри wallet-транзакции нет. Недоступность аналитики не мешает начислениям и покупкам.

Приём требует analytics `0001_analytics_inbox`. Analytics сохраняет неизменяемый источник transactionId в своём inbox; worker пишет raw_game_events и существующий fact_economy_transactions. Суммы читаются с `FINAL` и отдельно по currency_code. Полные sourceType/sourceId/itemIndex сохранены в raw payload; sourceId награды забега также доступен в raw run_id. Эти факты отражают движение игровой валюты и не являются выручкой реальных платежей.

HTTP 202/PENDING не завершает доставку. Wallet проверяет APPLIED, исходный messageId и весь receipt: transactionId, accountId, payloadHash, eventIds. Потерянный ответ и частичная запись ClickHouse повторяются с прежними идентификаторами. Аренда 30 секунд, экспоненциальная задержка до 300 секунд, максимум 20 автоматических отправок; исчерпанная аренда без подтверждения тоже переводится в REVIEW. Устаревшая аренда не подтверждает и не откатывает новую.

Повтор REVIEW без финансового эффекта, из backend/infra:

```sh
docker compose exec analytics python -m vatican_analytics.cli.replay_events <transactionId> --event-name wallet.transaction.analytics
docker compose exec wallet python -m vatican_wallet.cli.replay_analytics <transactionId>
```

Первую команду выполняйте только если проекция analytics тоже в REVIEW. Состояние получателя: защищённый `GET analytics /internal/sources/wallet-transaction/{transactionId}`; состояние отправителя — outbox_messages, event_name=wallet.transaction.analytics. Payload и суммы при replay не пересоздаются. Применение: сначала обновить analytics/migrate/schema, затем wallet/migrate/API/worker и перезагрузить gateway для `/v1/wallet/*`. Перенос legacy после этой миграции требует отдельного согласованного backfill импортированных ledger-строк; сам replay награды не является историческим импортом аналитики.

Постоянные улучшения: миграция `0001_wallet_initial` добавляет prestige_payment_receipts, сохраняя прежние балансы и команды. Внутренний `prestige-upgrade.payment` v1 содержит accountId, purchaseId, code, rulesetId, fromLevel, toLevel, currencyId=PRESTIGE и amount>0. Цена/уровень выбираются game_backend; wallet проверяет структуру, списывает только PRESTIGE и сохраняет immutable источник/receipt вместе с транзакцией PRESTIGE_UPGRADE. Повтор с другим messageId не списывает второй раз; изменённый источник или alias даёт 409. InsufficientFunds возвращает транспортный APPLIED с бизнес-outcome=REJECTED/rejectionCode=InsufficientFunds и сохранённым балансом. code в чеке остаётся идентификатором улучшения. Wallet не хранит игровые уровни и не применяет бонусы. Успех/отказ атомарны с receipt и consumed message; потеря ответа после commit безопасно повторяется. Обычный wallet.refund для магазинных PURCHASE не возвращает это списание: восстановление применения выполняет game worker.

Команда `prestige-upgrade.refund` v1 возвращает исходные списания за уровни одним пакетом. Wallet сверяет сумму и владельца покупок со своим журналом и проводит возврат `PRESTIGE_UPGRADE_REFUND` по каждой покупке. Повтор запроса с тем же resetId возвращает исходный чек. Новая операция с уже возвращённой покупкой отклоняется.

## Внутренние команды и gRPC

В общем Compose `wallet_commands` принимает команды из RabbitMQ, `wallet_grpc:50052` отдаёт их сохранённый статус game/shop. Оба контейнера используют собственную БД wallet и запускаются автоматически. Клиентские HTTP-маршруты остаются прежними; токены проверяются через auth gRPC. Для запуска отдельно от Compose:

```sh
python -m vatican_wallet.migrate
# Отдельные процессы; настройки RabbitMQ смотрите в .env.example:
python -m vatican_wallet.grpc_server
python -m vatican_wallet.broker_worker
```

Миграция `0001_wallet_initial` добавляет inbox/карантин без изменения балансов. Очереди `vatican.wallet.commands` и `vatican.wallet.dead` — quorum/persistent, routing key `wallet`; имена префиксов настраиваются. Результат сохраняется в inbox и читается через gRPC: отдельная очередь результатов без consumer не создаётся. Повтор сообщения с другим payload помещается в карантин; бизнес-ключи сохраняются при переходе со старого HTTP транспорта.

Поддерживаются прежние команды наград/кейсов/улучшений, а также `wallet.purchase.charge` / `wallet.purchase.refund` v1 с `{accountId, operationId, amount}`. Списание и возврат имеют разные стабильные messageId. `APPLIED` доставки может содержать бизнес-отказ `REJECTED` в receipt: магазин проверяет оба уровня. Подтверждение публикации в RabbitMQ не завершает покупку.

Не более 20 попыток, lease 60 секунд, backoff до 300 секунд. Обработка сохранённых команд продолжается при недоступности брокера. Внутренние HTTP методы с `X-Service-Secret`:

- `GET /internal/broker/commands/{message_id}` — статус, hash, результат и попытки.
- `POST /internal/broker/commands/{message_id}/replay` — явный повтор REJECTED/REVIEW с новым поколением и прежним источником.
- `POST /internal/broker/events/{event_id}/replay` — повтор исчерпанной dead-letter публикации.

`VD_AUTH_TRANSPORT=http` и `VD_WALLET_TRANSPORT=http` у вызывающих сервисов — только явный режим совместимости; автоматического fallback после сбоя RPC/брокера нет. Для TLS отдельного запуска доступны `VD_WALLET_GRPC_CERT_FILE`, `VD_WALLET_GRPC_KEY_FILE`, клиентский `VD_WALLET_GRPC_CA_FILE`.

Общий путеводитель по Swagger, доступу и примерам запросов: [../SWAGGER.ru.md](../docs/SWAGGER.ru.md).
