# Локальная инфраструктура Vatican Defendor

Запустите бэкенд и Unity Dedicated Server одной командой из корня репозитория.

```sh
./backend/infra/up.sh
```

Нужны Docker Compose и Python 3. Для сборки нужен Unity Editor версии из `ProjectSettings/ProjectVersion.txt` с поддержкой Dedicated Server для macOS или Linux. Скрипт ищет Editor в каталоге Unity Hub. Путь можно задать через `VD_UNITY_EDITOR` в `backend/infra/.env` или в окружении.

Команда собирает Dedicated Server при первом запуске, при отсутствии сборки и при изменении содержимого `Assets`, `Packages` или `ProjectSettings`. Сборка выполняется в отдельной копии проекта, поэтому открытый редактор можно оставить запущенным. Копия, готовые артефакты и `build.log` находятся в `backend/.runtime/dedicated-online`. При повторном запуске с теми же исходниками используется готовый артефакт. Для запуска вручную подготовленной сборки можно явно указать `VD_LOCAL_SERVER_EXECUTABLE`. Этот режим пропускает автоматическую сборку.

Скрипт создаёт локальный слот в `backend/infra/.env`, если такого слота ещё нет, поднимает Compose и проверяет HTTP-сервисы. Затем запускает выделенный сервер, который сам регистрируется в бэкенде. Успешный запуск подтверждает свежую регистрацию с выбранной версией контента и протоколом. Локальный сервер слушает `127.0.0.1:7979/udp`. Порт задаётся через `VD_LOCAL_SERVER_PORT`. Повторный вызов переиспользует работающий сервер и существующие секреты. Если собрана новая версия, команда переключает свободный слот на неё. Если сервер занят матчем, повторите запуск после освобождения слота.

Команду можно вызвать по абсолютному пути из любого каталога. Для запуска только бэкенда используйте `./backend/infra/up.sh --backend-only`.

В Docker Desktop стек отображается одной группой **vatican-defendor**, контейнеры — `vatican-defendor-<service>-1`. Фиксированные глобальные `container_name` не используются. `COMPOSE_PROJECT_NAME` меняет имя группы, `VD_DATA_NAMESPACE` — префикс постоянных томов. На текущей машине сохранены `VD_DATA_NAMESPACE=vatican-backend` и `VD_DATA_EXTERNAL=true` (готовые внешние тома), чтобы использовать уже существующие данные после переименования. Не меняйте этот префикс для заполненного стека без переноса данных.

## Состав

- API: `auth`, `game_backend`, `wallet`, `inventory`, `shop`, `rating`, `analytics`.
- Workers: `game_rewards`, `game_rating_results`, `wallet_worker`, `inventory_worker`, `shop_worker`, `rating_worker`, `analytics_worker`.
- PostgreSQL: семь отдельных БД сервисов и отдельная БД метаданных Superset.
- RabbitMQ с management UI, ClickHouse, MinIO (S3), Superset и его Redis.
- Caddy gateway — единый Backend URL клиента.
- Одноразовые миграции всех семи сервисов, создание схемы ClickHouse, инициализация S3 bucket и Superset.

При запуске создаются 38 контейнеров: 28 постоянно работающих и 10 одноразовых. Скрипт проверяет успешное завершение `*_migrate`, `analytics_schema`, `shop_media_init`, `superset_init`, затем удаляет эти временные контейнеры. В Docker Desktop остаются только 28 работающих контейнеров. Следующий запуск снова создаёт и выполняет инициализаторы; данные сохраняются. Проверка запуска требует, чтобы каждый worker работал, каждый healthcheck был healthy, а каждый одноразовый контейнер завершился с кодом 0.

Если локальный стенд ещё пуст, команда применяет тестовые настройки энергии и XP из примеров game_backend, а для нового ContentRelease задаёт награду в 1 единицу за уровень церкви. Для матча сервер использует ContentRelease из сборки. Для загрузки контента клиентами с другой версией опубликуйте релиз через API контента.

## Адреса и порты

Все внешние порты привязаны только к `127.0.0.1`, используют диапазон `28xxx` и переопределяются в `.env`. Внутренние порты и DNS сервисов остаются стандартными.

| Сервис | HTTP / Swagger | PostgreSQL | База / пользователь |
| --- | --- | --- | --- |
| game_backend | http://127.0.0.1:28080/docs | 28432 | vatican_game / game |
| auth | http://127.0.0.1:28081/docs | 28433 | vatican_auth / auth |
| wallet | http://127.0.0.1:28082/docs | 28434 | vatican_wallet / wallet |
| inventory | http://127.0.0.1:28083/docs | 28435 | vatican_inventory / inventory |
| inventory_grpc | gRPC 127.0.0.1:28093 | — | использует БД inventory |
| shop | http://127.0.0.1:28084/docs | 28436 | vatican_shop / shop |
| rating | http://127.0.0.1:28085/docs | 28437 | vatican_rating / rating |
| analytics | http://127.0.0.1:28086/docs | 28438 | vatican_analytics_inbox / analytics_inbox |

| Компонент | Адрес / порт | Переменная `.env` |
| --- | --- | --- |
| **Unity Backend URL / gateway** | **http://127.0.0.1:28087** | `VD_GATEWAY_HOST_PORT` |
| Superset | http://127.0.0.1:28088 | `VD_SUPERSET_HOST_PORT` |
| RabbitMQ AMQP | 28672 | `VD_RABBITMQ_HOST_PORT` |
| RabbitMQ management | http://127.0.0.1:28673 | `VD_RABBITMQ_MANAGEMENT_PORT` |
| ClickHouse HTTP | http://127.0.0.1:28123 | `VD_CLICKHOUSE_HTTP_HOST_PORT` |
| ClickHouse native | 28902 | `VD_CLICKHOUSE_NATIVE_HOST_PORT` |
| S3 / MinIO API | http://127.0.0.1:28900 | `VD_SHOP_S3_HOST_PORT` |
| MinIO console | http://127.0.0.1:28901 | `VD_SHOP_S3_CONSOLE_PORT` |

Пароль Superset admin — `VD_SUPERSET_ADMIN_PASSWORD`, доступ к MinIO — `VD_SHOP_S3_ACCESS_KEY` / `VD_SHOP_S3_SECRET_KEY`, RabbitMQ — `VD_RABBITMQ_USER` (по умолчанию `inventory`) / `VD_RABBITMQ_PASSWORD`. Пароли находятся в игнорируемом Git файле `backend/infra/.env`, права файла — 0600.

Gateway направляет `/v1/auth/*` и `/v1/guests` в auth, wallet в wallet, инвентарь/оружие в inventory, магазин/покупки в shop, рейтинг в rating, профиль/энергию/прогрессию/забеги — в game_backend.

### Подключение Unity

Команда создаёт корневой `.env` с текущим адресом gateway, если файла ещё нет. Адрес можно задать через `VD_BACKEND_URL`. В редакторе выберите **VaticanDefendor → Network → Play Local Online**. Unity перечитывает `.env` при каждом запуске онлайн-режима.

Адрес выбирается в таком порядке: `-vdBackendUrl`, `VD_BACKEND_URL` из окружения процесса, корневой `.env` в режиме **Play Local Online**, затем `NetworkDeployment`, если такой ресурс есть. Клиентская и серверная сборки получают параметры из окружения процесса. Для HTTP на loopback нужен `VD_INSECURE_LOCAL_BACKEND=true` или флаг `-vdInsecureLocalBackend`. Внешний gateway требует HTTPS и может запускаться через `compose.online.yaml`.

Для внешнего развёртывания предусмотрен отдельный [запуск Dedicated Server через Docker и DTLS](../unity_dedicated_server/README.md).

MinIO хранит данные в постоянном томе. Bucket `vatican-shop-media` и публичное чтение `shop/items/*` создаёт `shop_media_init`; анонимная запись/listing закрыты. Внутренний endpoint — `http://shop-s3:9000`, клиентский media URL по умолчанию строится из `VD_SHOP_S3_HOST_PORT` и `VD_SHOP_S3_BUCKET`; явный `VD_SHOP_MEDIA_BASE_URL` имеет приоритет.

Superset включает восемь тематических дашбордов и read-only доступ к ClickHouse. Подробности: [superset/README.ru.md](superset/README.ru.md), [PERFORMANCE.ru.md](../docs/infra/superset/PERFORMANCE.ru.md), [DEMO.ru.md](../docs/infra/superset/DEMO.ru.md).

## Управление

Доставка wallet ledger требует wallet `0001_wallet_initial` и analytics `0001_analytics_inbox`. Сначала обновите analytics API/worker и выполните analytics_migrate/analytics_schema, затем wallet_migrate и wallet/wallet_worker. Исторические операции ставятся в outbox без изменения балансов. Wallet не ждёт доступности аналитики при изменении денег; worker подтверждает событие после проекции ClickHouse. Повторы REVIEW и контракт описаны в [wallet/README.md](../wallet/README.md).

Из корня репозитория:

```sh
./backend/infra/up.sh check   # Проверка HTTP и регистрации Dedicated Server.
./backend/infra/up.sh status  # Контейнеры текущего стека и PID Dedicated Server.
./backend/infra/up.sh logs
./backend/infra/up.sh down    # Остановка сервера и Compose с сохранением томов.
tail -f backend/.runtime/dedicated-online/server.log
```

Эквивалентный ручной запуск после создания `.env`, из `backend/infra`:

```sh
docker compose up -d --build --wait --wait-timeout 600
docker compose ps -a
docker compose logs -f game_backend game_rewards shop_worker

# Повторная миграция / создание auth администратора.
docker compose run --rm game_migrate
docker compose exec auth python -m vatican_auth.cli.bootstrap_admin --login ваш_логин
```

Все сервисы определены непосредственно в единственном основном `backend/infra/compose.yaml`. Старые фрагменты и совместимые точки входа в каталогах отдельных сервисов удалены. Для запуска одной подсистемы укажите сервис в основном Compose, например `docker compose up -d rabbitmq`. `compose.online.yaml` — отдельный необязательный HTTPS overlay, используемый только при внешнем развёртывании. Не нужно перечислять несколько Compose-файлов или запускать workers вручную. При обновлении существующей старой игровой схемы используйте порядок переноса из README game_backend; данные и очереди не удаляйте.

Для игрового старта после миграций опубликуйте параметры энергии командой `vatican_backend.cli.configure_energy` (пример в README game_backend) и задайте её ID в `VD_ENERGY_RULESET_ID` локального `.env`. Настройка `VD_ASSIGNMENT_LEASE_SECONDS` задаёт срок первого ACK; очистку неподтверждённых назначений выполняет `game_rewards`.

Для получения подарков нужны текущие head game_backend/wallet и начальная полная схема inventory (`0001_inventory_initial`). Валюту и предметы доставляет `game_rewards`; ENERGY и EFFECT применяются локально при получении подарка. После REVIEW оператор повторяет отдельные сообщения через `vatican_backend.cli.replay_rewards <message_uuid>`. Команды публикации наборов и контракт API приведены в README game_backend.

Для кейсов те же API и game_rewards доставляют `case.consume` в inventory, `case.payment` в wallet и `case.reward` владельцам наград. После подтверждения списания worker сохраняет розыгрыш/гарант и продолжает выдачу. Публикация: `python -m vatican_backend.cli.publish_case --file case-example.json` после публикации наборов и правил.

Доставка рейтинга через RabbitMQ требует game_backend `0001_initial` и rating `0001_rating_initial`. Общий environment game_backend/game_rewards содержит VD_RATING_URL=http://rating:8080 и VD_RATING_BOARD_CODE (по умолчанию run-xp). Рейтинг доставляет тот же game_rewards через outbox; обязательного ожидания rating при старте игры нет. Состояние доступно отдельно в ratingStatus/rating; после REVIEW используйте replay_rewards с rating.messageId. Начальная миграция rating создаёт полную схему и доску run-xp; для перехода со старых ревизий локальную БД rating нужно пересоздать.

Аналитика после забега: задайте отдельный VD_ANALYTICS_INBOX_DB_PASSWORD; analytics_db хранит только inbox/aliases, отдельно от игровых данных и ClickHouse. analytics_migrate применяет `0001_analytics_inbox`, analytics_schema создаёт отсутствующие ClickHouse-таблицы, analytics принимает HTTP, analytics_worker записывает факты. Game environment содержит VD_ANALYTICS_URL=http://analytics:8080. API на порту VD_ANALYTICS_HOST_PORT (по умолчанию 28086). Health API проверяет durable inbox, поэтому временная недоступность ClickHouse не мешает приёму. Событие подтверждается game worker только после полной проекции; 202 означает PENDING, а не окончательное подтверждение. При REVIEW внутри analytics используйте replay_events <runId>, а после REVIEW game worker — replay_rewards <messageId>. Никакие прежние тома для запуска удалять не нужно.

Аналитика подарков/кейсов требует analytics `0001_analytics_inbox` и init_db для fact_case_settlements. Сначала остановите/обновите analytics API и analytics_worker, примените migrate/init_db и запустите их, затем обновите game_backend/game_rewards. Прежние run источники/receipts сохраняются. Для REVIEW подарка/кейса аналитический replay_events принимает --event-name level-gift.analytics / case.analytics и UUID операции; игровой replay_rewards использует analytics.messageId. Удаление томов не требуется.

Аналитика подтверждённых покупок shop требует analytics `0001_analytics_inbox`, init_db для `fact_shop_purchases` и миграции shop `0001_shop_initial` (и последующие). Сначала обновите analytics schema/API/worker, затем shop schema/API/shop_worker. Shop environment уже содержит VD_ANALYTICS_URL=http://analytics:8080. Shop worker отправляет только завершённые покупки, независимо от их бизнес-обработки; analyticsStatus не блокирует Completed. При REVIEW: analytics replay_events <purchaseId> --event-name shop.purchase.analytics, затем при необходимости shop replay_analytics <accountId> <operationId>. Эти команды не списывают валюту и не выдают предметы. Старые чеки/тома сохраняются; исторического backfill нет.

RabbitMQ inventory: задайте отдельный `VD_RABBITMQ_PASSWORD` (и при необходимости VD_RABBITMQ_USER/VHOST) из `.env.example`. Общий Compose добавляет `rabbitmq` с собственным томом и `inventory_worker`; AMQP — 127.0.0.1:28672, management — 127.0.0.1:28673. Inventory использует единую начальную схему `0001_inventory_initial`; прежнюю dev-базу после свёртки миграций нужно пересоздать. RabbitMQ общий с другими сервисами: при очистке inventory не удаляйте общий том или exchanges. Из этого каталога:

```sh
docker compose up -d --build rabbitmq inventory_migrate inventory inventory_worker
docker compose logs -f inventory_worker
```

Для отдельного запуска брокера из `backend/infra`: `docker compose up -d rabbitmq`. Worker принимает команды и публикует сохранённые результаты/dead-letter; кратковременные сбои повторяет сам, после REVIEW используйте inventory CLI replay_broker. Контракт, настройки внешнего AMQP и правила повторов — в [inventory/RABBITMQ.ru.md](../docs/inventory/RABBITMQ.ru.md). Game/shop workers продолжают текущую HTTP-доставку.

Рейтинг по умолчанию использует `VD_RATING_TRANSPORT=rabbitmq`: `game_rewards` публикует transactional outbox с publisher confirms, `rating_worker` сохраняет inbox/применяет XP и отправляет результат, `game_rating_results` подтверждает его в игровой БД. Общий `rabbitmq` из compose.yaml используется вместе с inventory; routing key рейтинга — `rating`, очереди — `vatican.rating.commands/results/dead`. Для существующего прямого API можно явно выбрать `http`. Workers зависят только от миграций своей БД и восстанавливают соединение с брокером самостоятельно; недоступность одного получателя не блокирует запуск доставки другим. Публикация ещё не означает COMPLETED: нужен проверенный APPLIED-результат. Порядок повторов/REVIEW и CLI redrive — в ../rating/README.md.

Постоянные улучшения включаются отдельно от запуска стека: опубликуйте конфигурацию через `vatican_backend.cli.configure_prestige`, задайте VD_PRESTIGE_RULESET_ID в `.env` и обновите API/game_rewards. Этот параметр передаётся общей средой обоим процессам; wallet должен быть обновлён с consumer `prestige-upgrade.payment`. Для игроков с бонусами требуется отдельная обновлённая Unity dedicated-сборка, объявляющая prestigeSchemaVersion=1; стек не собирает Unity автоматически. Настройка, API и восстановление после REVIEW описаны в game_backend/README.md.

Межсервисные транспорты по умолчанию: auth и чтения inventory/wallet — gRPC; команды wallet/inventory/rating — RabbitMQ. `wallet_commands` обрабатывает финансовые команды, `wallet_grpc` отдаёт их результаты внутри сети Compose. Никаких дополнительных процессов вручную поднимать не нужно. Аудит и причины выбора HTTP для контента/аналитики — `backend/docs/INTERSERVICE_AUDIT.ru.md`.

Общий указатель API, авторизация и примеры первых запросов: [SWAGGER.ru.md](../docs/SWAGGER.ru.md).
