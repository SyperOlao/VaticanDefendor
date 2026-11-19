# VaticanDefendor game backend

Начальная миграция `0001_initial` создаёт игровую схему, включая аудит dedicated-результатов с FK на `runs.id`. Миграция `0002_profile_name_lookup` добавляет индекс поиска ников к существующей схеме и сохраняет данные профилей. Команда `python -m vatican_backend.migrate` применяет обе миграции до актуальной версии.

Игровой профиль, контент и правила, назначение dedicated server, checkpoint, восстановление и фиксация результатов. Авторизация выполняется через auth; баланс и предметы обслуживают wallet и inventory.

Из `backend/game_backend`:

```sh
python -m pip install -r requirements-dev.txt
# Задайте VD_DATABASE_URL, VD_AUTH_URL, VD_WALLET_URL, VD_INVENTORY_URL и VD_SERVICE_SECRET.
python -m vatican_backend.migrate
python -m vatican_backend
python -m vatican_backend.worker
```

`VD_SERVER_POOL_JSON` и `VD_RUN_REWARDS_JSON` задают контролируемые серверы и утверждённые награды. Пустой пул позволяет запустить API без Unity-сервера. Пример остальных настроек: `.env.example`.

Доставка inventory в общем Compose: команды через RabbitMQ (`VD_GAME_INVENTORY_TRANSPORT=rabbitmq`), снимок выбранного оружия и результаты по gRPC (`VD_INVENTORY_READ_TRANSPORT=grpc`, `VD_INVENTORY_GRPC_TARGET=inventory_grpc:50051`). Источником повторов остаётся игровой outbox; публикация не означает завершённую выдачу. Для явной совместимости доступны режимы http, автоматического переключения после ошибки нет. Настройки, IDL и восстановление: [inventory gRPC/RabbitMQ](../docs/inventory/GRPC.ru.md).

Профиль пользователя (Bearer-токен auth):

- `GET /v1/profile` — accountId, version, displayName, avatarAssetKey, displayTimezoneIana, locale.
- `GET /v1/profile/avatars` — готовые аватары: assetKey, title, symbol, backgroundColor.
- `PUT /v1/profile` — совместимое переименование и необязательные настройки.
- `PATCH /v1/profile` — частичное обновление; хотя бы одно редактируемое поле обязательно.

Пример обновления (expectedVersion берите из последнего GET):

```json
{"expectedVersion":1,"displayName":"Рыцарь","avatarAssetKey":"avatars/knight","displayTimezoneIana":"Europe/Moscow","locale":"ru"}
```

Пропущенные поля сохраняются, `avatarAssetKey: null` сбрасывает аватар. Ник, аватар и настройки меняются атомарно с одной версией; при HTTP 409 перечитайте профиль и повторите осознанное изменение. Ник: 1–32 символа, до 120 байт UTF-8, NFC, без управляющих символов. Сервер принимает только аватары из `profile-avatars.json`; `VD_PROFILE_AVATARS_FILE` позволяет выбрать другой файл. Начальный каталог — три буквенных аватара с заданным цветом. После изменения каталога перезапустите API. Загрузка своей фотографии не входит в этот контракт.

Новых миграций для профиля нет: используются прежние profiles/account_preferences, для самого профиля миграция не требуется. Обновите зависимости и код API, включая файл каталога. Unity показывает отдельные snapshots энергии/XP/кошелька и позволяет редактировать ник/аватар; для одного адреса сервисов используйте gateway из README infra. Подробная сверка реализованного и оставшегося: [FUNCTIONAL_REVIEW.ru.md](../docs/game_backend/FUNCTIONAL_REVIEW.ru.md).

Энергия читается через `GET /v1/energy` с Bearer-токеном. Параметры публикуются явно и затем выбираются настройкой `VD_ENERGY_RULESET_ID`. Пример для локального тестирования:

```sh
python -m vatican_backend.cli.configure_energy --ruleset-id energy-local-v1 \
  --max-energy 100 --regen-interval-ms 60000 --energy-per-tick 1 --run-cost 5
export VD_ENERGY_RULESET_ID=energy-local-v1
```

Опубликованные параметры не изменяются: для нового баланса создайте другой ruleset ID. Старая конфигурация без стоимости старта требует явного заполнения через эту команду. Пока версия не выбрана, новые забеги не запускаются; ошибка — `EnergyRulesUnavailable`.

Старт забега списывает энергию один раз для исходного `runId`. Неподтверждённое назначение истекает через `VD_ASSIGNMENT_LEASE_SECONDS` (по умолчанию 30 секунд), а `vatican_backend.worker` освобождает слот и возвращает списание. После первого ACK возврат по этому таймауту не выполняется. Повтор исходного `requestKey` возвращает прежний забег, включая состояние `failed`; новая попытка требует нового ключа.

Эффекты регенерации выдаются операторской командой; один `source-key` не продлевает эффект при повторе:

```sh
python -m vatican_backend.cli.grant_regen_effect <account_uuid> \
  --source-key operator:boost-001 --rate-bonus 0.5 --duration-seconds 3600
```

XP и уровень читаются через `GET /v1/progression`. Для каждого нового игрового релиза опубликуйте правила до создания забегов:

```sh
python -m vatican_backend.cli.configure_progression --file progression-example.json
```

`progression-example.json` содержит локальный тестовый баланс. Задайте XP, лимиты и пороги уровней. Версия XP может отличаться от контента: dedicated server передаёт её `rulesetId` при `ready` и `recover`. Если поле опущено, используется `contentReleaseId`, и правила нужно публиковать с тем же ID. Для подарков укажите UUID существующих `reward_bundles` в `rewardBundleId`. Опубликованная версия неизменяема; без неё новый забег вернёт `ProgressionRulesUnavailable`.

Dedicated server передаёт в результат поле `stats`, например:

```json
{"heroKills":{"orc":4},"towerKills":{"orc":5},"buildingUpgrades":{"church":3}}
```

XP вычисляется сервером и возвращается в `progression` вместе с новым totalXp, уровнем и открытыми подарками. Подарки открываются со статусом `AVAILABLE`; получение запускается через API подарков ниже. Повтор результата возвращает сохранённую прогрессию исходного забега; изменение счётчиков после принятия результата даёт HTTP 409. Старый формат без `stats` означает нулевые боевые счётчики, поэтому может дать только XP за победу. Старые забеги без закреплённых правил XP завершаются без начисления XP задним числом.

Рейтинг после забега:

- В доску `run-xp` поступает `progression.xpGranted` за этот забег, включая ноль. Накопленный `totalXp` не прибавляется повторно.
- Задайте `VD_RATING_URL` (локально `http://127.0.0.1:8085`, в Compose `http://rating:8080`). `VD_RATING_BOARD_CODE=run-xp` выбирает доску для новых результатов; маршрут и очки уже принятого результата остаются в outbox.
- Результат и `GET /v1/runs/{run_id}` возвращают `ratingStatus`: PENDING, COMPLETED, REVIEW или SKIPPED, и объект `rating` с messageId, boardCode, points, attempts и сохранённым receipt. `rewardStatus` отражает только wallet/inventory и не ждёт rating.
- Worker доставляет `run.rating` v1 после commit результата. Недоступность rating не отменяет XP и выдачу других наград. Потерянный ответ, повтор результата и новый messageId для того же runId не начисляют очки заново.
- После 20 попыток или постоянного отказа статус — REVIEW. Исправьте причину и выполните `python -m vatican_backend.cli.replay_rewards <message_uuid>` с ID из `rating.messageId`; исходные очки и доска сохраняются.

Текущим бинарникам для обоих транспортов нужны миграции game_backend `0001_initial` и rating `0001_rating_initial`. HTTP-путь использует общий сервисный секрет; RabbitMQ — отдельные credentials/vhost и обработчик результатов. Единственная начальная миграция rating создаёт полную схему и доску `run-xp` с источником RUN_XP; прежнюю локальную схему нужно пересоздать. Для другой доски сначала выполните в сервисе rating `python -m vatican_rating.cli.configure_board --code season-2`, затем задайте новый VD_RATING_BOARD_CODE в game_backend. Несовместимый источник уже существующей доски не заменяется автоматически.

Старые завершённые результаты и забеги без закреплённых правил XP возвращают SKIPPED: автоматического начисления рейтинга задним числом нет. `receipt.scoreAfter` — исторический счёт после исходной операции; текущий рейтинг читается через `GET /v1/leaderboards/{code}` в rating.

Аналитика после забега:

- Задайте VD_ANALYTICS_URL (локально http://127.0.0.1:8086, в Compose http://analytics:8080) и общий сервисный секрет.
- После нового принятого результата в той же транзакции создаётся один неизменяемый outbox snapshot run.analytics. В нём только игровые UUID, версии правил, серверное время, исход, счётчики и XP/уровни. Ник, токены, оружие и произвольный клиентский payload не отправляются.
- Результат и GET /v1/runs/{run_id} возвращают analyticsStatus PENDING/COMPLETED/REVIEW/SKIPPED и объект analytics с messageId, attempts и receipt. Состояния рейтинга и наград независимы.
- COMPLETED означает подтверждённую запись всего набора событий/фактов worker в ClickHouse. Ответ 202 от analytics означает только durable inbox и остаётся PENDING в game_backend. Повтор результата и потерянный ответ не создают новый источник или новые ключи фактов.
- При кратковременном сбое повторы автоматические. После 20 попыток или постоянного отказа используйте replay_rewards с analytics.messageId. Если REVIEW возник внутри analytics, сначала повторите источник через его replay_events <runId> и восстановите проекцию.

Для этого этапа нужна версия game_backend с поддержкой analytics (отдельной миграции game_backend для analytics нет), PostgreSQL inbox analytics `0001_analytics_inbox`, ClickHouse init_db и запущенные game_rewards/analytics_worker. В общем Compose требуется новый отдельный VD_ANALYTICS_INBOX_DB_PASSWORD. Старые принятые результаты не пересылаются автоматически и показывают SKIPPED. Подробности, семантика FINAL и команды — в README analytics.

Аналитика подарков и кейсов:

`level-gift.analytics` создаётся в транзакции перехода подарка в CLAIMED, `case.analytics` — в транзакции перехода кейса в COMPLETED после подтверждения всех наград. Частичная выдача и REJECTED не создают событие успеха. Это время завершения выдачи, а не время первоначального запроса/розыгрыша. Локальные ENERGY/EFFECT используют тот же путь завершения.

POST/GET отдельного подарка и открытия кейса возвращают `analyticsStatus` и `analytics` с messageId/receipt. PENDING/REVIEW аналитики не меняет уже завершённую выдачу; SKIPPED означает, что событие ещё не создано либо операция историческая. Повтор получения, открытия или сообщения не меняет исходные данные события и не выдаёт награды заново.

Сначала обновите analytics до миграции `0001_analytics_inbox`, выполните его init_db и запустите обновлённые API/worker, затем обновите game_backend и game_rewards. Дополнительной миграции game_backend для этих событий не требуется. ClickHouse получает факт полученного подарка и новую fact_case_settlements. Событие кейса содержит только идентификаторы, выбранную строку, rarity, pity, режим оплаты и время завершения; seed и балансы не передаются. Исторические завершённые подарки/кейсы автоматически не пересылаются.

После REVIEW в analytics:

```sh
python -m vatican_analytics.cli.replay_events <claim_uuid> --event-name level-gift.analytics
python -m vatican_analytics.cli.replay_events <opening_uuid> --event-name case.analytics
```

При REVIEW в game_backend после восстановления проекции выполните `replay_rewards <analytics.messageId>`. При обычном временном сбое оба worker продолжают автоматически.

Получение подарков:

- `GET /v1/level-rewards` — список доступных, выполняющихся и полученных подарков.
- `GET /v1/level-rewards/{claim_id}` — статус и результаты отдельных строк.
- `POST /v1/level-rewards/{claim_id}/claim` — получить подарок; тело не требуется. Нужен Bearer-токен auth. ID подарка служит ключом повтора.

Ответ HTTP 202 означает `PENDING`/`REVIEW`, HTTP 200 — `CLAIMED`. Валюта и предметы доставляются worker отдельно; `deliveryStatus=PARTIAL` показывает частично выполненную выдачу. Подтверждённые строки сохраняются при недоступности другого сервиса. `REVIEW` требует исправления причины и повторного запуска неуспешных `messageId` через `vatican_backend.cli.replay_rewards`.

Для публикации нового набора после первого запуска API (когда каталог оружия зарегистрирован):

```sh
python -m vatican_backend.cli.publish_reward_bundle --file level-reward-bundle-example.json
```

Пример предназначен для локального тестирования. Задайте собственные `bundleId`, `code`, `version`, содержимое и ссылку `rewardBundleId` в настройке уровня. Публикация той же версии с тем же содержимым идемпотентна; изменение требует новой версии и ID.

Поддерживаются CURRENCY (`PRESTIGE`, `GEMS`), WEAPON (до 100 экземпляров на подарок), ITEM, CASE, ENERGY (ref `ENERGY`) и EFFECT (`ENERGY_REGEN_RATE_BONUS`). Энергия начисляется локально, включая overcap. Неподдерживаемый тип, пустой набор, некорректные параметры эффекта или неизвестное оружие/кейс отклоняют получение до выдачи любых строк. Состав подарка фиксируется при открытии уровня; повтор запроса и смена содержимого исходного набора не создают новую выдачу.

Пример строки временного эффекта:

```json
{"itemIndex":1,"rewardType":"EFFECT","rewardRef":"ENERGY_REGEN_RATE_BONUS","quantity":1,
 "effect":{"modifierValue":"0.500000","durationSeconds":3600}}
```

`modifierValue` — положительная десятичная строка с точностью до 6 знаков, меньше 10¹²; `0.5` даёт +50% скорости регенерации. `durationSeconds` — целое число от 1 до 31 536 000. `quantity` для EFFECT всегда 1. Несколько отдельных строк/подарков суммируют бонусы, каждая имеет собственный срок. Другие типы эффектов пока не поддержаны.

Эффект начинает действовать при получении подарка по серверному времени, без пересчёта прошедшего времени с новым бонусом. Запись эффекта и подтверждение строки сохраняются в одной транзакции. Повтор после истечения эффекта, частичный сбой wallet/inventory и повторная доставка не меняют его ID, начало или окончание. В `receipt` возвращаются `effectId`, `effectType`, `modifierValue`, `startsAt`, `expiresAt`. Подарок только с EFFECT сразу возвращает HTTP 200; смешанный подарок может оставаться в PARTIAL/REVIEW после применения эффекта.

Готовый отдельный пример новой версии набора:

```sh
python -m vatican_backend.cli.publish_reward_bundle --file level-reward-effect-example.json
```

Укажите его `bundleId` в новом наборе правил прогрессии; существующие открытые подарки сохраняют прежний снимок. Операторский `grant_regen_effect` не принимает `source-key`, начинающиеся с `level-reward:` или `case-reward:` — эти префиксы принадлежат выдаче наград.

Примените миграции game_backend (до `0001_initial`), wallet и inventory и запустите `vatican_backend.worker` для доставки. Для EFFECT новых миграций wallet/inventory не требуется.

Открытие кейсов:

- `GET /v1/cases` — опубликованные определения и цены (`limit`/`offset`).
- `POST /v1/cases/{case_id}/open` — Bearer-токен, тело `{"requestKey":"UUID","paymentMode":"INVENTORY"}`. По умолчанию списывается один `case:<code>` из inventory. Режим `WALLET` вместо кейса списывает опубликованную цену в PRESTIGE/GEMS; цена должна быть положительной.
- `GET /v1/case-openings/{opening_id}` — состояние оплаты, сохранённое выпадение, строки награды и подтверждения.

`requestKey` уникален для игрока: повтор возвращает то же открытие, изменение caseId или режима даёт HTTP 409. Пока открытие этого кода кейса не завершено, другой ключ также даёт 409. После завершения новый ключ означает новое открытие и новое списание. Клиент не задаёт цену, шансы, гарант или награду.

HTTP 202 означает `FUNDING`, `PENDING` или `REVIEW`; повтор завершённой операции возвращает HTTP 200 (`COMPLETED`/`REJECTED`). Недостаток кейсов или средств фиксируется как `REJECTED` без розыгрыша и изменения гаранта. Пополнение после отказа требует нового ключа запроса. После успешного списания сбои выдачи продолжаются с тем же результатом: автоматический возврат не выполняется, чтобы частичная выдача не оставалась бесплатной. Неоднозначный сбой или исчерпание повторов сохраняют REVIEW и исходные ID сообщений; после устранения причины используйте `vatican_backend.cli.replay_rewards <message_uuid>` для неуспешных сообщений.

Состав кейса, все наборы наград и секретный seed фиксируются перед списанием. Розыгрыш использует целочисленные веса. Каждое подтверждённое списание увеличивает счётчик неудач для кода кейса; естественное выпадение `guaranteedRarity` сбрасывает его. Когда следующий бросок достигает `pityThreshold`, выбор идёт только среди строк с этой точной rarity с их относительными весами. Гарант сохраняется при смене версии правил. Счётчик и результат записываются вместе; повторы не меняют их.

Публикация локального примера после регистрации каталога оружия:

```sh
python -m vatican_backend.cli.publish_reward_bundle --file level-reward-bundle-example.json
python -m vatican_backend.cli.publish_reward_bundle --file level-reward-effect-example.json
python -m vatican_backend.cli.configure_progression --file progression-example.json
python -m vatican_backend.cli.publish_case --file case-example.json
```

В `case-example.json` задайте свой caseId, стабильный code, опубликованный rulesetId, цену, гарант и drops. `reward` в каждой строке — точный снимок уже опубликованного набора. Повтор публикации неизменяем; новый баланс требует нового caseId и rulesetId. Поддержаны все типы наград подарков, включая локальные ENERGY/EFFECT; эффект кейса имеет отдельный источник `case-reward:`. Обновите game_backend до `0001_initial`, wallet/inventory до `0001_inventory_initial`, запустите worker. Старые открытия миграция сохраняет завершёнными и не выдаёт повторно.

Для общего запуска из `backend/infra`:

```sh
docker compose up -d --build
docker compose logs -f game_backend game_rewards
```

Swagger: http://127.0.0.1:8080/docs. Профиль и клиентский API забегов принимают Bearer-токен auth. `POST /v1/runs` принимает `requestKey` UUID и возвращает HTTP 202; состояние запрашивается через `GET /v1/runs/{run_id}`. Результат содержит `rewardStatus`: `PENDING`, `COMPLETED` или `FAILED`. Изменения экипировки в inventory применяются к следующему забегу; текущий использует сохранённый снимок.

Проверка с отдельной тестовой PostgreSQL-базой:

```sh
VD_TEST_DATABASE_URL='postgresql://user:password@localhost:5432/vatican_test' python -m pytest -q tests
```

Повторная доставка неуспешной награды после устранения причины:

```sh
python -m vatican_backend.cli.replay_rewards <message_uuid>
```

## LOCAL-забеги

Backend поддерживает LOCAL без выделенного Unity-сервера. Нужны миграции через `0001_initial` (обычный `python -m vatican_backend.migrate` применяет весь актуальный head), API и `python -m vatican_backend.worker`. Миграция сохраняет старые результаты и не начисляет исторические награды. Unity-клиент LOCAL в эту реализацию не входит.

Перед стартом опубликуйте ContentRelease обычным внутренним API контента, правила XP через `configure_progression` и конфигурацию энергии через `configure_energy`. Создайте собственный файл по `local-run-example.json`, заменив contentReleaseId на реальный опубликованный release и rulesetId на опубликованные совместимые правила XP. Карта, сложность, сроки, лимиты и награды задаются оператором. Значения примера служат образцом настройки, а не утверждённым балансом игры.

```sh
python -m vatican_backend.cli.configure_local_runs --file local-run-config.json
```

Публикация проверяет зависимости и известное оружие награды. policyId неизменяем: та же конфигурация повторяется безопасно, изменение требует нового ID. LOCAL не использует VD_RUN_REWARDS_JSON; ставка награды входит в LOCAL policy. Настройка энергии остаётся общей с dedicated через VD_ENERGY_RULESET_ID.

Все маршруты требуют Bearer-токен auth:

| Запрос | Назначение |
| --- | --- |
| `POST /v1/local-runs` | `{ "requestKey": "UUID", "policyId": "local-river-v1" }` — запуск |
| `GET /v1/local-runs/active` | Восстановление активного забега после перезапуска клиента |
| `GET /v1/local-runs/{runId}` | Контекст, проверка, сохранённый итог и доставка наград |
| `POST /v1/local-runs/{runId}/finish` | Сдача результата по схеме ниже |
| `POST /v1/local-runs/{runId}/abandon` | Закрытие несданного забега без наград |

Запуск возвращает runId, sessionId, policyId, contentReleaseId, rulesetId, mapId, difficulty, startedAt, submissionDeadline, energyCost и закреплённый профиль/loadout. Он атомарно сохраняет run/context, снимки правил и запись списания энергии. Повтор requestKey возвращает исходный runId даже после завершения и при недоступном inventory; замена policyId или режима тем же ключом даёт 409. На аккаунте разрешён один активный забег, включая dedicated. Свежий LOCAL-старт требует доступного inventory, повтор — нет. LOCAL начинается сразу: отказ, отклонение или истечение срока не возвращают потраченную энергию.

Пример тела finish; идентификаторы и параметры берите из ответа start:

```json
{
  "schemaVersion": 1,
  "sessionId": "11111111-1111-4111-8111-111111111111",
  "policyId": "local-river-v1",
  "contentReleaseId": "sha256-REPLACE_WITH_PUBLISHED_CONTENT",
  "rulesetId": "xp-local-v1",
  "mapId": "river",
  "difficulty": "normal",
  "outcome": "victory",
  "churchLevel": 5,
  "durationMs": 60000,
  "stats": {
    "heroKills": {"orc": 2},
    "towerKills": {},
    "buildingUpgrades": {"church": 4}
  }
}
```

Сервер проверяет владельца, session/context, срок сдачи, длительность относительно серверного времени, минимальную/максимальную длительность, уровень церкви для victory/defeat, разрешённые цели, лимиты и общую частоту счётчиков. Клиент не передаёт награду, цену энергии, XP или время начисления. Проверка LOCAL устанавливает правдоподобность сводки, но не доказывает честность симуляции. После принятия и начисления LOCAL использует общий рейтинг и mode-aware run.analytics v1; условия доставки описаны ниже.

Некорректное тело возвращает 422 без сохранения результата. Корректная по схеме, но отклонённая сводка возвращает 200 с validationStatus/settlementStatus REJECTED и validationReasons; XP и награды не выдаются. Первая сводка и аудит сохраняются неизменяемо, для принятой и отклонённой. Точный повтор возвращает прежний итог; другой payload того же runId даёт 409. Порядок ключей JSON не меняет идентичность запроса.

Принятая сводка сначала фиксируется отдельно, затем одна транзакция сохраняет XP, уровни/открытые подарки, версию профиля, receipt и outbox wallet/inventory/rating/analytics. При сбое начисления GET показывает ACCEPTED/PENDING; повтор finish или worker завершает сохранённый результат, даже после submissionDeadline. Менять результат или отменять принятый забег нельзя. Победа даёт prestigePerChurchLevel × churchLevel, поражение — без престижа; оба исхода получают XP по закреплённым правилам и одно закреплённое оружие через существующий inventory consumer.

settlementStatus COMPLETED означает локальное начисление. rewardStatus PENDING/COMPLETED/FAILED отдельно описывает доставку wallet/inventory; FAILED требует исправления причины и ручного повторного запуска сообщения. При частичном сбое или потерянном подтверждении повторяется исходное сообщение с тем же runId; consumers не начисляют его заново. После исчерпания попыток используйте существующий `python -m vatican_backend.cli.replay_rewards UUID` и worker. Внутренние ошибки начисления остаются ACCEPTED/PENDING для повторной обработки; worker логирует только тип ошибки, причину конфигурации/ёмкости нужно устранить оператору. Просроченные несданные LOCAL-забеги закрываются worker, чтением LOCAL или перед следующим стартом. Старые LOCAL без validation snapshot завершаются только отказом без наград.


## Рейтинг и аналитика LOCAL

Новый принятый LOCAL-результат при начислении атомарно создаёт `run.rating` и `run.analytics` вместе с XP и наградами. Отклонённый результат, отказ от забега и просроченная несданная сводка не создают эти сообщения. До завершения начисления состояния доставки — SKIPPED, после — PENDING/COMPLETED/REVIEW независимо друг от друга и от rewardStatus.

Рейтинг получает **xpGranted этого забега**, включая ноль, через существующий контракт v1 на доску `VD_RATING_BOARD_CODE` (по умолчанию `run-xp`). Dedicated и LOCAL используют одну настроенную доску. Исходный boardCode и очки сохраняются в outbox; изменение настройки не перенаправляет уже созданные сообщения. Применение rating дедуплицируется по runId, даже если транспортный messageId поменялся. Существующий транспорт rating, HTTP либо RabbitMQ с обработчиком результатов, работает и для LOCAL.

В аналитический payload добавлен `simulationMode: "LOCAL"`. Создаются `run.completed`, `xp.granted` и при переходе уровня `player.level_up`. `fact_run_settlements.simulation_mode` хранит режим, `fact_xp_transactions.source_type` — LOCAL_RUN. Новые dedicated-сообщения сохраняют прежний формат без поля режима; analytics интерпретирует его отсутствие как DEDICATED. Старые payload/hash, messageId и eventId не пересчитываются. Смена режима уже принятого аналитического источника даёт 409.

При сбое rating/analytics награды, энергия и прогрессия не повторяются и не откатываются. Worker повторяет неизменяемый snapshot. Потерянное подтверждение после удалённого commit безопасно: rating возвращает прежний scoreAfter, analytics — прежний receipt; проекция ClickHouse повторяет стабильные ключи. Чтение фактов требует FINAL. analytics HTTP 202 подтверждает только inbox; game сохраняет PENDING до полного APPLIED. После 20 неудач — REVIEW.

Порядок обновления:

1. Разверните обновлённый код analytics, выполните в `backend/analytics` `python -m vatican_analytics.init_db`, затем запустите обновлённые analytics API/worker. Команда повторяемо добавляет simulation_mode с DEFAULT 'DEDICATED' в существующую таблицу без удаления фактов; нужны права ALTER. Новых PostgreSQL-миграций для этого этапа нет.
2. Обновите game_backend API и `vatican_backend.worker`; обычные миграции сервисов должны быть применены до актуального head. Для rating оставьте его существующие workers/настройки транспорта.
3. Проверьте состояние по `GET /v1/local-runs/{runId}` либо повтору finish: rating/analytics содержат messageId, attempts и receipt.

После исправления причины REVIEW выполните `python -m vatican_backend.cli.replay_rewards MESSAGE_UUID` и оставьте game worker запущенным. Если сам analytics inbox находится в REVIEW, сначала выполните в analytics `python -m vatican_analytics.cli.replay_events RUN_UUID` и дайте analytics worker завершить проекцию, затем при необходимости повторите game message.

Исторические завершённые LOCAL, у которых этих outbox-сообщений не было, по-прежнему возвращают SKIPPED: чтение/повтор finish не запускает автоматический backfill. Принятый, но ещё не начисленный LOCAL после обновления восстанавливается worker и создаёт оба сообщения в транзакции начисления.

Доставка рейтинга через RabbitMQ включается `VD_RATING_TRANSPORT=rabbitmq` (в общем Compose — по умолчанию). Задайте `VD_RATING_RABBITMQ_URL` либо общие `VD_RABBITMQ_HOST/PORT/USER/PASSWORD/VHOST` и одинаковые queue/exchange prefixes для game и rating. `python -m vatican_backend.worker` публикует команду после claim/commit outbox и сохраняет publisher confirm отдельно от окончательного результата; `python -m vatican_backend.rating_worker` получает результат и атомарно сохраняет receipt/дедупликацию, затем ACK. `rating.brokerPublishedAt` и `rating.resultGeneration` показывают этап доставки. Пока проверенный APPLIED не пришёл, статус PENDING; REJECTED/REVIEW требуют разбора, запоздавший APPLIED может завершить такую операцию. Другие получатели остаются на прежних HTTP-адаптерах. Нет сетевых вызовов внутри игровой транзакции. Повторы после потерянного confirm/ACK используют прежний messageId/runId и не начисляют XP заново. Протокол, worker rating и redrive: ../rating/README.md.

## Постоянные улучшения за престиж

Реализованы каталог, покупка следующего уровня, однократная оплата в wallet и применение постоянного процентного бонуса. Состав и цены публикуются оператором, не задаются клиентом. Конфигурация `prestige-example.json` — пример баланса: пять уровней, цены 100/200/300/400/500 и суммарный бонус 5/10/15/20/25%. В PDF указаны направления и пять уровней, но этих цен и процентов там нет.

Поддержанные цели текущей симуляции:

| target | stat |
| --- | --- |
| HERO | MOVE_SPEED, ATTACK_SPEED, HEALTH, ATTACK_DAMAGE |
| ARCHER (башня Tower) | ATTACK_SPEED, HEALTH, ATTACK_DAMAGE |
| WALL | HEALTH |

Неизвестные цели/характеристики, в том числе пока не реализованные постоянные улучшения требушетов и движение стационарных башен, отклоняются при публикации до оплаты. Один code всегда обозначает одну пару target/stat, одна пара не получает второго code. Значение modifierValue — **суммарная** доля бонуса на купленном уровне: `0.100000` означает ×1.1, а не прибавку ко всем предыдущим уровням. Уровни идут подряд 1–5, бонус строго растёт, максимум 5.0 (+500%), цена — положительное целое PRESTIGE.

Развёртывание:

1. Обновите wallet и примените `python -m vatican_wallet.migrate` до `0001_wallet_initial` или актуального head. Обновите его API: новый внутренний consumer обрабатывает `prestige-upgrade.payment` v1.
2. Примените игровые миграции через `0001_initial`: `python -m vatican_backend.migrate`. Старые уровни, определения, балансы и чеки сохраняются; миграции не покупают улучшения автоматически.
3. Опубликуйте свою конфигурацию: `python -m vatican_backend.cli.configure_prestige --file prestige-example.json`. Повтор той же версии безопасен, изменение требует нового rulesetId.
4. Задайте `VD_PRESTIGE_RULESET_ID=prestige-v1` для API и game worker и перезапустите их обновлённый код. В общем Compose параметр передаётся из `backend/infra/.env`. Без выбранной опубликованной конфигурации каталог available=false, покупка недоступна.
5. Обновите Unity dedicated server до сборки с `prestigeSchemaVersion=1` в ready/recover. Игрок с купленным бонусом назначается только на поддерживающий его сервер. При отсутствии такого процесса старт возвращает 503 до списания энергии. Серверы без флага продолжают обслуживать аккаунты без бонусов.

API требует Bearer-токен auth:

- `GET /v1/prestige-upgrades`: активный rulesetId, доступные уровни/цены, currentLevel/nextLevel, уже купленные bonuses и pendingPurchase для восстановления после перезапуска клиента.
- `POST /v1/prestige-upgrades/purchases`: тело ниже; 202 для PENDING/REVIEW, 200 для сохранённого COMPLETED/REJECTED.
- `GET /v1/prestige-upgrades/purchases/{purchaseId}`: только своя операция, её paymentMessageId, attempts, paymentReceipt и результат.

```json
{"requestKey":"11111111-1111-4111-8111-111111111111","rulesetId":"prestige-v1","code":"hero.attack_damage","expectedLevel":0}
```

expectedLevel берите из каталога. Клиент не передаёт accountId, цену, новый уровень или силу бонуса. Один requestKey возвращает исходную операцию, даже если активная конфигурация сменилась; изменение payload тем же ключом даёт 409. Новый ключ с прежним expectedLevel после покупки также даёт 409. Пока покупка PENDING/REVIEW, другая покупка улучшений аккаунта блокируется. Максимальный уровень не создаёт нового списания.

План с ценой, предыдущим/следующим уровнем и бонусом вместе с outbox сохраняется **до сети**. Game worker отправляет payment v1 в wallet. Wallet атомарно сохраняет списание, полный immutable receipt по purchaseId и alias messageId. Новый messageId с тем же источником не списывает повторно; замена владельца, цены, code или уровней даёт 409. InsufficientFunds — сохранённый REJECTED, повтор старого намерения после пополнения не списывает деньги: требуется новый requestKey. В чеке code остаётся кодом улучшения, причина отказа — rejectionCode.

Только после проверенного APPLIED game_backend одной транзакцией повышает уровень, сохраняет бонус, повышает profile.version и подтверждает outbox. При сбое после удалённого списания применение и подтверждение откатываются вместе; worker повторяет тот же источник, получает прежний чек и применяет бонус один раз. Автоматического возврата при временном сбое применения нет. Потерянные/неверные подтверждения повторяются с backoff; после 20 попыток — REVIEW без нового списания. Исправьте причину и выполните `python -m vatican_backend.cli.replay_rewards PAYMENT_MESSAGE_UUID`.

Купленные бонусы действуют **со следующего старта**, включая покупку во время текущего забега. Dedicated assignment/recovery получает закреплённый prestigeJson; LOCAL start/get — prestigeBonuses. Снимок хранится в Run и не меняется от последующих покупок/балансировок. Инвентарь и исходные определения оружия не переписываются. Unity применяет бонусы к базовым характеристикам героя, рассчитанному оружию и определениям будущих построек до старта симуляции: здоровье/урон/движение умножаются, интервал атаки делится на multiplier. Повторное применение к тому же ECS World отклоняется. Recovery создаёт исходный снимок в новом World, затем восстанавливает checkpoint, не удваивая бонусы. Старые забеги без снимка получают пустой набор.

Исторические player_prestige_upgrades без bonus_snapshot сохраняют уровень, помечаются requiresReview в каталоге и требуют явного согласования данных перед следующей покупкой этого code. Миграция не угадывает их параметры и не создаёт бонус/списание задним числом. Отдельный экран покупки в Unity этим API-этапом не добавлен; применение на dedicated server реализовано. LOCAL-клиент должен использовать полученный snapshot при инициализации своей симуляции, отдельной Unity-интеграции LOCAL по-прежнему нет.

## Swagger: описание API для разработки и ручной проверки

После обновления/перезапуска game_backend откройте [Swagger UI](http://127.0.0.1:28080/docs) или [ReDoc](http://127.0.0.1:28080/redoc). Это стандартный порт общего Compose; при своём порте используйте `/docs` на адресе игрового API. Через gateway по умолчанию документация доступна на `http://127.0.0.1:28087/docs`. Машинная спецификация — `/openapi.json`.

Документация содержит русские названия и разделы, объяснения каждого поля, схемы ответов, выбираемые примеры запросов/ответов, ошибок и состояний 200/202/REVIEW. В начале есть последовательности работы с LOCAL/dedicated и покупками, а также объяснение безопасных повторов requestKey. Для метода покупки можно переключать примеры «Оплачено и применено», «Недостаточно престижа», «Покупка сохранена» и REVIEW. Явный `null` в примере сброса аватара сохраняется в OpenAPI.

В **Authorize → HTTPBearer** вставьте accessToken auth (либо гостевой token) без слова Bearer. DedicatedServerSecret и ContentPublisherSecret — отдельные способы доступа для внутренних операций, они не заменяют токен игрока. API других микросервисов описаны в их собственных Swagger; game_backend не объявляет чужие маршруты.

UUID и версии в примерах демонстрационные: подставляйте ID из своих каталогов и ответов. Для публикации контента/checkpoint показана сокращённая оболочка с корректной контрольной суммой; полноценный игровой payload экспортируется Unity. Описания ответов OpenAPI не добавляют фильтрацию реальных JSON-ответов. Для обновления документации миграции БД не нужны.
