# Магазин VaticanDefendor

Каталог и асинхронные покупки оружия за престиж через wallet и inventory. Добавлен отдельный TEST/dev MOCK-конвейер внешней оплаты consumable-наборов оружия: заказ, receipt/webhook, независимая сверка провайдера и идемпотентная выдача. Настройка, API и восстановление: [MOCK_PAYMENTS.ru.md](../docs/shop/MOCK_PAYMENTS.ru.md). Реальные провайдеры ещё не подключены.

Сверка исходных требований и оставшиеся блоки: [FUNCTIONAL_REVIEW.ru.md](../docs/shop/FUNCTIONAL_REVIEW.ru.md).

Swagger с русскими пояснениями, схемами, примерами запросов/ответов и пошаговыми сценариями:
[SWAGGER.ru.md](../docs/shop/SWAGGER.ru.md). В локальном Compose — http://127.0.0.1:28084/docs;
MOCK-разделы видны при явном включении TEST/dev.

Обычные покупки в общем Compose используют RabbitMQ для выдачи inventory и gRPC для проверки результата (`VD_SHOP_INVENTORY_TRANSPORT=rabbitmq`, `VD_INVENTORY_READ_TRANSPORT=grpc`). Платёжный шаг wallet и отдельный TEST/dev MOCK-конвейер сохраняют свои контракты. Настройки и восстановление: [RabbitMQ](../docs/shop/RABBITMQ.ru.md), [gRPC inventory](../docs/inventory/GRPC.ru.md).

Из `backend/shop`:

```sh
python -m pip install -r requirements-dev.txt
# Задайте переменные из .env.example в окружении процесса.
python -m vatican_shop.migrate
VD_PORT=8084 python -m vatican_shop
```

Общий Compose содержит сервис на http://127.0.0.1:28084/docs. Клиентские методы принимают Bearer-токен auth:

- `GET /v1/shop` — каталог, версия и доступность с учётом лимитов игрока.
- `POST /v1/purchases` — покупка; тело: `operationId` (ненулевой UUID), `offerId`, `catalogVersion` из каталога.
- `GET /v1/purchases/{operation_id}` — состояние своей покупки.
- `GET /v1/purchases?limit=20&before=<operationId>` — история от новых к старым; `nextCursor` передаётся в `before` следующей страницы.

Покупка возвращает `Pending` (HTTP 202), затем `Completed`, `Rejected` или `Review`. Цена определяется сервером и сохраняется при приёме запроса. Повтор использует исходный `operationId` и те же параметры, в том числе после смены каталога. `Pending`, `Completed` и `Review` занимают лимит покупки. Чужие операции недоступны.

Completed передаёт сохранённую `inventoryRevision`, если она получена от inventory. После покупки GET /v1/weapons в inventory возвращает выданный экземпляр и ревизию не ниже подтверждённой.

Worker повторяет временно не подтверждённые команды и возвращает престиж после сохранённого отказа inventory. Конфликт участника переводит операцию в `Review` без автоматического возврата. После проверки участников повторяется сохранённый шаг:

```sh
python -m vatican_shop.worker
python -m vatican_shop.cli.replay_purchase <account_uuid> <operation_uuid> --expected-version <job_version> --reason "Участники проверены" --request-key <command_uuid>
```

Для выдачи через RabbitMQ задайте `VD_SHOP_INVENTORY_TRANSPORT=rabbitmq` и настройки брокера из `.env.example`. По умолчанию используется `http`; charge/refund wallet остаются HTTP. Настройка общего Compose, RPC-результаты и восстановление: [RABBITMQ.ru.md](../docs/shop/RABBITMQ.ru.md).

Администратор auth может разбирать покупки через API:

- `GET /v1/admin/shop/purchases?status=Review&accountId=<uuid>&limit=50` — фильтры состояния и аккаунта. `nextCursor` передаётся в `before`; без status возвращаются все состояния.
- `GET /v1/admin/shop/accounts/{account_id}/purchases/{operation_id}` — сохранённый план, состояние задания, число попыток, версия и `canRetry`.
- `GET /v1/admin/shop/accounts/{account_id}/purchases/{operation_id}/events?after=0&limit=50` — журнал в порядке `sequence`; `nextCursor` передаётся в `after`.
- `POST /v1/admin/shop/accounts/{account_id}/purchases/{operation_id}/retry` — повтор исходного шага из Review.

Тело повтора:

```json
{
  "expectedVersion": 5,
  "requestKey": "761f06e2-4a61-4fca-a6fa-aef78a9d3113",
  "reason": "Проверены кошелёк и выдача; можно продолжить исходную операцию"
}
```

Берите `expectedVersion` из `job.version` и создавайте новый `requestKey` для каждой новой команды. После потери ответа повторите тот же UUID и то же тело: вернётся сохранённое подтверждение, даже если покупка уже завершилась. Актуальное состояние смотрите через GET. Изменение тела с тем же UUID или устаревшая версия возвращают 409. Повтор запускается только для Review с сохранённым допустимым планом; цена, получатель и предмет не передаются заново. CLI тоже проверяет ADMIN и запрашивает токен скрыто. В журнале сохраняются автор, UUID команды и причина. Сырые ответы участников, SQL и тексты исключений в журнал не записываются.

Для пустой dev-базы используется единая начальная миграция `0001_shop_initial`; журнал, версии заданий и восстановление входят в неё. Старую dev-схему необходимо пересоздать перед запуском API и worker. См. [SCHEMA.ru.md](../docs/shop/SCHEMA.ru.md). После исчерпания 20 попыток, включая падение worker до фиксации ответа, требуется разбор через Review.

Подтверждённая покупка создаёт `shop.purchase.analytics` v1 в той же транзакции, что завершает чек. Событие содержит сохранённые offer/catalogVersion, цену в PRESTIGE, конфигурацию и подтверждённый экземпляр оружия. Оно появляется только после подтверждения wallet и inventory; бесплатная покупка имеет сумму 0. Отклонённые, возвращённые и частично выполненные покупки не создают событие успеха. Смена каталога не меняет принятый snapshot.

Чек, повтор POST и история возвращают `analyticsStatus` и `analytics`: `SKIPPED` до завершения или для старого чека без события, `PENDING` при доставке, `COMPLETED` после полной проекции, `REVIEW` при постоянном отказе или после 20 попыток. Бизнес-статус покупки остаётся `Completed`, даже если analytics недоступен. Shop worker обслуживает покупки и аналитику независимо, с арендами и backoff. HTTP выполняется вне транзакции БД. Подтверждение проверяется по purchaseId/accountId, hash payload и точным eventIds.

`purchaseId = UUIDv5(accountId, "shop.purchase:" + operationId)`: одинаковые operationId разных игроков независимы. Неизменяемый источник в analytics и стабильные ключи ClickHouse предотвращают повторный учёт при потере ответа и частичной записи. Для count/SUM требуется `FINAL`. Покупки за престиж записываются в `fact_shop_purchases`, отдельно от выручки внешних платежей.

Задайте `VD_ANALYTICS_URL` (локально http://127.0.0.1:8086; в Compose http://analytics:8080) и общий `VD_SERVICE_SECRET`. Для обновления сначала примените analytics `0001_analytics_inbox` и `init_db`, обновите analytics API/worker, затем shop API/worker и его миграции. Поля доставки outbox входят в начальную схему shop `0001_shop_initial`; создание схемы выполняется командой `migrate`.

После устранения причины REVIEW повторите только аналитику:

```sh
python -m vatican_analytics.cli.replay_events <purchase_uuid> --event-name shop.purchase.analytics
# Если shop также исчерпал попытки:
python -m vatican_shop.cli.replay_analytics <account_uuid> <operation_uuid>
```

purchase_uuid есть в `analytics.receipt.purchaseId` после подтверждения, а до него вычисляется по формуле выше или берётся из защищённого outbox. `replay_analytics` не повторяет списание и выдачу; `replay_purchase` предназначен для отдельного восстановления самой покупки.

Каталог хранится в PostgreSQL. После начальной миграции `0001_shop_initial` каталог пуст. Существующий JSON можно один раз импортировать командой ниже; токен администратора auth вводится скрыто. Повтор импорта пропускает совпадающие товары и отклоняет конфликтующие изменения целиком. Файл больше не читается при запросах; `VD_SHOP_CATALOG_FILE` не используется.

```sh
python -m vatican_shop.cli.import_catalog shop-catalog.json
```

Управление товарами оружия за престиж доступно с Bearer-токеном роли `ADMIN` в auth:

- `GET /v1/admin/shop/offers?limit=50&after=<id>&includeDeleted=false` — список; `nextCursor` передаётся в `after`.
- `GET /v1/admin/shop/offers/{id}` — товар с ревизией, включая удалённый.
- `POST /v1/admin/shop/offers` — создать товар.
- `PUT /v1/admin/shop/offers/{id}` — заменить редактируемые поля с `expectedRevision`.
- `DELETE /v1/admin/shop/offers/{id}?expectedRevision=1` — удалить из каталога.
- `POST /v1/admin/shop/images` — загрузить PNG/JPEG сырым телом запроса с соответствующим `Content-Type`; до 8 MiB и 4096 × 4096 пикселей. Доступно только ADMIN.

Пример тела создания:

```json
{
  "id": "offer_sword_rare",
  "weaponConfigurationId": "sword.rare",
  "title": "Редкий меч",
  "price": 120,
  "purchaseLimit": 0,
  "available": true,
  "unavailableReason": ""
}
```

Для PUT передайте те же поля без `id` и добавьте `expectedRevision` из ответа сервера. `purchaseLimit: 0` означает отсутствие лимита; `available: false` отключает продажу. Несовпавшая ревизия возвращает 409. Удалённый ID нельзя использовать повторно; история и принятые покупки сохраняются. Новый или изменённый идентификатор оружия проверяется по game_backend. Определения оружия создаются в игровом сервисе.

Загрузка изображения возвращает `imageKey`, `imageUrl`, `contentType`, `sizeBytes`, `width`, `height`. Передайте `imageKey` при создании или редактировании товара. Сервер проверяет наличие объекта в S3 до транзакции БД. Произвольные URL и внешние S3-ключи не принимаются. Каталог и административные ответы возвращают готовый `imageUrl`; в БД хранится только ключ. При PUT отсутствие поля сохраняет картинку, `imageKey: null` убирает привязку. Старый файл остаётся доступен по прежней ссылке.

Из `backend/shop` загрузить локальные картинки через административный API можно так (токен вводится скрыто):

```sh
python -m vatican_shop.cli.upload_images media/generated/*.png
python -m vatican_shop.cli.import_catalog shop-catalog.json
```

Актуальные иллюстрации и их SHA-256 перечислены в [media/generated/manifest.json](media/generated/manifest.json). В той же папке сохранены промпты встроенного image_gen и отчёт проверки S3. `shop-catalog.json` содержит ключи актуальных изображений. Перед импортом в новое пустое хранилище сначала загрузите PNG. Одинаковые байты дают тот же ключ. Товары могут создаваться без картинки.

MinIO проекта использует bucket `vatican-shop-media`, API http://127.0.0.1:19002 и консоль http://127.0.0.1:19003. Задайте S3-переменные из `.env.example`; `VD_SHOP_S3_ENDPOINT_URL` — адрес для backend, `VD_SHOP_MEDIA_BASE_URL` — публичный адрес для клиентов (домен/CDN для удалённых устройств). Бакет разрешает анонимный GET для `shop/items/*` и `avatars/*`. Изображения товаров получают ключ по SHA-256, аватары профиля — версионированный ключ. Для обоих используется неизменяемое кэширование.

Проверка с реальными auth и S3 включается переменными `VD_TEST_SHOP_S3_ENDPOINT_URL`, `VD_TEST_SHOP_S3_BUCKET`, `VD_TEST_SHOP_S3_ACCESS_KEY`, `VD_TEST_SHOP_S3_SECRET_KEY`, `VD_TEST_SHOP_MEDIA_BASE_URL` и запуском `tests/test_auth_admin_integration.py`. Она загружает три PNG и оставляет объекты в указанном bucket. `VD_TEST_SHOP_MEDIA_REPORT_FILE` позволяет сохранить отчёт с клиентским каталогом для проверки Unity `VaticanDefendor.Editor.OnlineBackendVerification.VerifyShopImages` (batchmode без `-quit`).

Версия клиентского каталога меняется при правке товаров или связанных определений оружия. Клиент со старой версией получает `CatalogChanged` и должен обновить каталог. Для обновления внутренних подтверждений покупки обновляйте shop, wallet и inventory вместе.

Тесты выполняются на PostgreSQL в изолированных схемах:

```sh
VD_TEST_DATABASE_URL='postgresql://user:password@localhost:5432/vatican_test' python -m pytest --rootdir=.. -q tests
```

Матрица сбоев, конкурентных повторов, остановки процессов и настройки живых интеграций: [RESILIENCE.ru.md](../docs/shop/RESILIENCE.ru.md).
