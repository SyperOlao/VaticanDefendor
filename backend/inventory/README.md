# Инвентарь VaticanDefendor

Предметы и кейсы игрока, экземпляры оружия, их опыт и экипировка. Определения оружия принадлежат game_backend; пользователя проверяет auth.

Из `backend/inventory` установите зависимости и экспортируйте переменные из `.env.example`. `VD_SERVICE_SECRET` должен совпадать с секретом внутренних вызовов других сервисов и содержать минимум 32 символа. Файл `.env` автоматически не загружается.

```sh
python -m pip install -r requirements-dev.txt
python -m vatican_inventory.migrate
VD_PORT=8083 python -m vatican_inventory
```

Swagger при локальном запуске командой выше: [http://127.0.0.1:8083/docs](http://127.0.0.1:8083/docs). В общем Compose порт по умолчанию — 28083: [Swagger inventory](http://127.0.0.1:28083/docs); его можно изменить через VD_INVENTORY_HOST_PORT.

В Swagger есть инструкция первого запроса, описания полей и примеры успешных ответов/отказов. В **Authorize → HTTPBearer** вставьте только токен auth (token гостя или accessToken сессии), без `Bearer`. Для внутренних методов используйте **Authorize → ServiceSecret**: значение VD_SERVICE_SECRET передаётся как X-Service-Secret. У POST /internal/messages список **Request body → Examples** содержит все шесть команд; UUID в примерах вымышленные, заменяйте их своими. OpenAPI JSON в общем Compose: [http://127.0.0.1:28083/openapi.json](http://127.0.0.1:28083/openapi.json).

| Метод | Назначение |
| --- | --- |
| `GET /v1/weapons` | Оружие, определения, loadout и Revision |
| `GET /v1/weapons/owned` | Страница принадлежащих игроку экземпляров без запроса определений |
| `GET /v1/weapons/{instance_id}` | Один принадлежащий игроку экземпляр с определением |
| `PUT /v1/weapons/loadout` | От одного до трёх различных InstanceIds в порядке слотов |
| `GET /v1/weapons/loadout/operations/{request_key}` | Сохранённый результат смены экипировки |
| `GET /v1/inventory` | Количества предметов `item:<ref>` и кейсов `case:<ref>`, revision |
| `GET /v1/inventory/{item_id}` | Один предмет и текущая ревизия; нулевой остаток сохраняется |
| `GET /internal/players/{account_id}/snapshot` | Закрепляемый снимок выбранного оружия для забега |
| `POST /internal/grants` | Идемпотентная выдача покупки по accountId/operationId/configurationId |
| `GET /internal/players/{account_id}/grants/{operation_id}` | Результат выдачи покупки без повторного обращения к контенту |
| `POST /internal/items/consumptions` | Идемпотентное расходование обычных предметов |
| `GET /internal/players/{account_id}/item-consumptions/{operation_id}` | Сохранённый результат расходования |
| `POST /internal/messages` | Версионированные команды выдачи и расходования |
| `GET /internal/broker/commands/{message_id}` | Состояние команды RabbitMQ, результат и доставка событий |
| `GET /internal/broker/commands` | Страницы команд с фильтром status |
| `GET /internal/broker/events`, `GET /internal/broker/events/{event_id}` | Доставка результатов и dead-letter, включая REVIEW |
| `GET /internal/broker/rejections`, `GET /internal/broker/rejections/{rejection_id}` | Карантин с кодом, hash и связанным eventId без исходного тела |
| `GET /health/live`, `GET /health/ready` | Доступность процесса и актуальность миграций |

Клиентские методы принимают Bearer-токен auth; внутренние — `X-Service-Secret`. В loadout можно передать необязательный `expectedRevision`: устаревшая ревизия возвращает 409. Повтор неизменённого loadout не повышает ревизию. Пример тела:

```json
{"InstanceIds":["a96dc6c2-a7a6-4f9a-8334-ea16e7db2d12"],"expectedRevision":2,"requestKey":"49ca66e1-3d56-469a-9714-72440b19e7c8"}
```

`requestKey` необязателен. С ним повтор возвращает исходный ответ и `loadoutOperation`, включая исходную ревизию, даже если контент недоступен или позже выбран другой loadout. Повтор не применяет старый выбор заново. Для нового выбора используйте новый ключ и актуальный expectedRevision; для текущего состояния — GET /v1/weapons. Другой payload с прежним ключом даёт 409. GET результата операции не загружает контент; чужие и неизвестные операции/экземпляры возвращают 404.

Для больших коллекций используйте `GET /v1/weapons/owned?limit=50&configurationId=axe.rare` и `GET /v1/inventory?limit=50&kind=ITEM`. Фильтры необязательны; kind допускает ITEM/CASE, limit — 1..100. Следующую страницу запрашивайте с полученным `cursor=<nextCursor>` и теми же фильтрами. `nextCursor: null` завершает обход. Курсор привязан к владельцу, фильтру и ревизии: изменение инвентаря между страницами возвращает 409, после которого обход нужно начать заново.

Ответ `/v1/weapons/owned` содержит Weapons, весь Loadout, Revision и nextCursor; определений Configurations в нём нет, поэтому чтение работает при недоступном game_backend. Snapshot и XP забега читают только выбранные/закреплённые экземпляры. `/v1/inventory` без параметров сохраняет прежний полный ответ items/revision; пагинация включается при передаче limit, cursor или kind. Нулевые остатки и исходные legacy-ключи предметов сохраняются.

`/internal/messages` принимает `messageId`, `eventName`, `schemaVersion=1` и `payload`. Поддержаны `run.weapon_reward`, `level.reward`, `case.reward`, `case.consume`, `inventory.item.consume` и `inventory.weapon.grant`. У последнего payload совпадает с POST /internal/grants. WEAPON создаёт отдельные экземпляры; ITEM/CASE пополняют количества. У `case.consume` внешний APPLIED означает обработку команды, а `receipt.outcome` — APPLIED либо REJECTED/InsufficientCases; сохранённый отказ остаётся отказом после пополнения.

Награда забега сохраняется по runId; новый messageId не повторяет оружие или XP. Опыт начисляется выбранным в исходном снимке экземплярам по закреплённым ExperiencePerLevel/MaximumLevel. Повторы сохранённых выдач не требуют доступного game_backend; изменение владельца или исходного payload конфликтует. Аналогичные ключи наград уровней и кейсов — claimId/itemIndex и openingId/itemIndex.

Успешная выдача покупки возвращает `instanceId` и `inventoryRevision`. Ревизия сохраняется в receipt и передаётся shop клиенту; последующее чтение содержит экземпляр и ревизию не ниже подтверждённой. Для старых receipts без ревизии она однократно фиксируется при первом чтении/повторе по текущему состоянию, без повторной выдачи.

Расходование обычного предмета разрешает игровой сервис, который отвечает за его применение. Внутренняя команда только списывает количество:

```json
{"accountId":"9ef8ec7c-2120-4825-a0cf-05fc01f97a1d","operationId":"3b321688-36fc-43f3-9819-21f08b994a92","itemId":"item:repair-kit","quantity":2}
```

Одинаковая команда доступна через POST /internal/items/consumptions и как payload сообщения `inventory.item.consume` v1. Ключ — accountId/operationId, общий для обоих путей. Ответ содержит status APPLIED/REJECTED, quantityAfter и при успехе inventoryRevision; нехватка фиксируется как REJECTED/InsufficientItems и не меняется после пополнения. У сообщения внешний status APPLIED означает обработку, бизнес-результат находится в receipt.status. Изменение itemId/quantity с тем же ключом даёт 409. Кейсы `case:*` расходуются только через `case.consume`; legacy-ключи обычных предметов сохраняются. Клиентского API списания нет. HTTP 404 при поиске результата не доказывает отмену параллельного запроса: повторяйте прежнюю команду с тем же ключом.

Для новой базы используется одна миграция `0001_inventory_initial`: все 13 таблиц, ограничения и индексы текущей модели. Промежуточные dev-миграции удалены; обновление прежней цепочки не поддерживается — такую тестовую БД нужно пересоздать. Миграция фиксирована в Alembic и не зависит от будущих изменений ORM. Повторный запуск на актуальной схеме ничего не меняет. Снимки входных команд хранятся в JSON с сохранением чисел и хэшей; TEST MOCK-режим самой миграцией не включается.

Приём через RabbitMQ выполняет отдельный worker. Задайте VD_INVENTORY_RABBITMQ_URL из .env.example:

```sh
python -m vatican_inventory.worker
python -m vatican_inventory.cli.publish_command --file command-example.json
python -m vatican_inventory.cli.inspect_broker commands --status REVIEW --limit 50
python -m vatican_inventory.cli.inspect_broker events --status REVIEW
python -m vatican_inventory.cli.inspect_broker rejections --code InvalidEnvelope
python -m vatican_inventory.cli.replay_broker <command_message_uuid>
python -m vatican_inventory.cli.replay_broker <outbox_event_uuid> --outbox
```

Пример команды предназначен для локальной проверки: задайте свои accountId/claimId/messageId. Подтверждение публикации не означает выдачу; результат приходит в очередь vatican.inventory.results. Общий Compose содержит rabbitmq и inventory_worker; задайте отдельный VD_RABBITMQ_PASSWORD. Протокол, очереди и восстановление после сбоев: [RabbitMQ](../docs/inventory/RABBITMQ.ru.md).

В общем Compose game_backend/shop отправляют обычные команды inventory через RabbitMQ, а снимок и сохранённые результаты читают через отдельный `inventory_grpc` (внутри сети inventory_grpc:50051, на хосте 28093). HTTP API сохраняется для клиента и ручной работы. Запуск gRPC отдельно: `python -m vatican_inventory.grpc_server`; проверка: `python -m vatican_inventory.cli.grpc_health`. IDL, настройки и порядок восстановления: [gRPC](../docs/inventory/GRPC.ru.md).

Тесты используют отдельные временные схемы PostgreSQL:

```sh
VD_TEST_DATABASE_URL='postgresql://user:password@localhost:5432/vatican_test' python -m pytest --import-mode=importlib -q tests
```

Для тестов реального брокера также задайте VD_TEST_RABBITMQ_URL на отдельный RabbitMQ 4.1+ vhost. Тесты создают и удаляют только очереди со случайными собственными префиксами.


TEST/dev MOCK-grant: отдельный внутренний `/internal/mock-payment-grants` с service secret и grant-ключом, выключен по умолчанию. Его receipts входят в начальную схему. В Swagger метод появляется только при включённом режиме и требует одновременно ServiceSecret и MockGrantSecret. Протокол и ограничения: [MOCK_PAYMENTS.ru.md](../docs/shop/MOCK_PAYMENTS.ru.md).
