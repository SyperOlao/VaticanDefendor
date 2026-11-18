from ..migrate import SCHEMA_REVISION
"""Human-facing OpenAPI; documents handlers without changing runtime validation."""
from copy import deepcopy

from fastapi.openapi.utils import get_openapi
from pydantic.json_schema import models_json_schema

from . import examples as ex
from . import response_schemas as s
from ..migrate import SCHEMA_REVISION

TAGS = [
    {'name': 'Каталог', 'description': 'Предложения оружия за престиж с доступностью для текущего игрока.'},
    {'name': 'Покупки за престиж', 'description': 'Принятие операции, проверка результата и история текущего аккаунта.'},
    {'name': 'Администрирование каталога', 'description': 'Только негостевой ADMIN: товары, ревизии и картинки S3.'},
    {'name': 'Восстановление покупок', 'description': 'Только ADMIN: диагностика, журнал переходов и повтор из Review.'},
    {'name': 'MOCK: игрок', 'description': 'Только TEST/dev: условная оплата consumable-наборов оружия без списания денег.'},
    {'name': 'MOCK: администратор', 'description': 'Нужны ADMIN и отдельный MockControlKey. Публикация продукта и управление симулятором.'},
    {'name': 'MOCK: webhook', 'description': 'Сигнал провайдера с HMAC-подписью; сам по себе не подтверждает оплату.'},
    {'name': 'Проверка сервиса', 'description': 'Без авторизации: состояние процесса и готовность схемы PostgreSQL.'},
]

INTRO = """
Магазин продаёт **оружие за престиж**. Сервис auth проверяет пользователя,
wallet списывает престиж, inventory выдаёт экземпляр оружия. Цена и награда задаются сервером.

## Начало работы

1. Получите токен в auth: `POST /v1/guests` → `token`, либо `POST /v1/auth/login` → `accessToken`.
   В обычном локальном Compose [Swagger auth](http://127.0.0.1:28081/docs); при других портах используйте адрес своего auth.
2. Нажмите **Authorize → PlayerBearer**, вставьте только токен, без слова `Bearer`.
3. В **GET /v1/shop** скопируйте `offers[].id` и **актуальное** поле `version`.
4. Выполните **POST /v1/purchases**: `offerId` из каталога, `catalogVersion` из `version`,
   новый ненулевой UUID `operationId`. Сохраните UUID до отправки.
5. Ответ **202 / Pending** означает приём операции. Проверяйте **GET /v1/purchases/{operation_id}**,
   пока `status` не станет `Completed`, `Rejected` или `Review`. Нужен работающий shop worker.

## Как читать результат покупки

| status | Значение и действие |
| --- | --- |
| Pending | Обработка продолжается. Запрашивайте receipt; новый operationId не нужен. |
| Completed | Покупка завершена; grantedWeaponInstanceIds содержит выданные экземпляры. |
| Rejected | Сохранённый отказ, например InsufficientFunds, CatalogChanged, OfferUnavailable, PurchaseLimitReached или GrantRejected. |
| Review | Результат внешнего шага требует проверки администратором; не создавайте покупку заново для «повтора». |

После таймаута повторяйте **тот же operationId и исходное тело**. Изменённое тело с тем же ID — 409.
Новый operationId означает новую покупку. `CatalogChanged` требует заново прочитать каталог и,
если игрок по-прежнему хочет купить, создать новую операцию. Пополнение баланса не изменяет старый Rejected.
`analyticsStatus` описывает доставку аналитики отдельно от результата покупки; PENDING/REVIEW аналитики
не отменяют уже выданное оружие. Валюта каталога `prestige`, завершённого receipt — `PRESTIGE`.

## Администратор

Используйте токен **негостевого аккаунта с текущей ролью ADMIN**. Роль проверяется через auth при каждом запросе.
Для правки/удаления копируйте `revision` товара в `expectedRevision`; устаревшая ревизия — 409.
Удаление сохраняет tombstone, ID не переиспользуется. Для восстановления покупки сначала прочитайте
административную карточку и журнал, затем передайте `job.version` как `expectedVersion`, новый UUID
`requestKey` и причину. Повтор этого requestKey с прежним телом безопасен.

## Примеры, ошибки и доступ

UUID, версии, цены и картинки ниже иллюстративны: они **не создают контент автоматически**.
Перед Try it out заменяйте ID и версии значениями из своего окружения. Курсор `nextCursor`
передаётся без правок; null означает конец списка.

Ошибки имеют единый формат **code, detail, requestId, errors**. `detail` и сообщения полей остаются
на английском. `requestId` совпадает с `X-Request-ID` ответа; его можно задать заголовком как UUID.
При 503 сохраните ключ операции и учитывайте `Retry-After`. HTTP 409 у POST покупки может быть
как receipt `Rejected`, так и обычной ошибкой конфликта: смотрите схему и примеры ниже.
Секреты окружения не являются токенами игрока и не заменяют роль ADMIN.
"""

MOCK_INTRO = """
## MOCK: проверить оплату без денег

MOCK **включён в этом API**. Маршруты доступны только при явном включении в test/development;
в production их нет. Поддержаны CONSUMABLE-наборы только WEAPON, всего до 20 экземпляров.

1. Оператор публикует неизменяемый reward bundle в game_backend через
   `python -m vatican_backend.cli.publish_reward_bundle --file <file.json>`.
   UUID/состав можно проверить `GET game_backend /v1/reward-bundles/{bundleId}`.
2. ADMIN с **PlayerBearer + MockControlKey** публикует `/v1/admin/shop/mock/products`.
   Берите `rewardBundleId` опубликованного набора; цена `199 USD` в minor units означает условные `1.99 USD`.
3. Игрок читает `/v1/mock/products`, создаёт `/v1/mock/orders` с productId и requestKey, сохраняет orderId.
4. ADMIN вызывает `simulate` со сценарием **SUCCESS**. Он получает `receipt` и `webhook`, но запрос сам webhook не отправляет.
5. Игрок передаёт **реальный** receipt в `confirm`. Либо TEST-инструмент отправляет точные байты webhook body
   с HMAC-SHA256 подписью `signature` из simulate. Для webhook в Authorize используется **MockWebhookSignature**, не control key.
6. Shop worker независимо проверяет запись провайдера; GET заказа в итоге показывает **FULFILLED / COMPLETED**.
   Confirm/webhook только запускают сверку. Подписанный сигнал не заменяет provider verification.

| scenario | Проверка |
| --- | --- |
| SUCCESS | Подтверждённая оплата и выдача. |
| DECLINED | Окончательный отказ, выдачи нет. |
| PENDING | Ожидание оплаты без выдачи. |
| TIMEOUT | Провайдер подтвердил оплату, SDK-ответ потерян: receipt=null; worker восстановит состояние без него. |
| REFUND | Возврат ранее подтверждённого платежа. До dispatch выдача отменяется; после dispatch — REFUNDED/REVIEW. |
| DUPLICATE_WEBHOOK | Возвращает тот же eventId/providerVersion для проверки повторных доставок. |

`providerUnavailable=true` отключает проверку провайдера, даже если webhook подписан. Чтобы восстановить,
выполните разрешённый simulate с новым requestKey и providerUnavailable=false.
После 20 неудачных попыток нужен административный retry с текущей `version` и причиной.
Завершённые единицы награды не выдаются заново. REFUND после отправки **не изымает оружие автоматически**,
сохраняет фактические receipts и требует Review; retry возврата запрещён.

Для разных consumable-покупок одного SKU нужны разные requestKey. Для повтора одного заказа — прежний ключ.
Тестовые control/webhook/receipt/grant ключи различны; не передавайте их игровому клиенту.
Receipt в примерах — обозначение, а нулевая signature показывает только формат hex64.
Они не являются действующим подтверждением: используйте значения настоящего ответа simulate.
"""


def response(model, description, **examples):
    return {'model': model, 'description': description,
            'content': {'application/json': {'examples': {key: ex.example(title, value) for key, (title, value) in examples.items()}}}}


def body(**examples):
    return {'content': {'application/json': {'examples': {key: ex.example(title, value) for key, (title, value) in examples.items()}}}}


def operation(tag, summary, description, responses, request=None):
    return {'tag': tag, 'summary': summary, 'description': description, 'responses': responses, 'request': request}


READ_RECEIPT = response(s.PurchaseReceipt, 'Текущее состояние покупки.',
    pending=('Обрабатывается', ex.PENDING), completed=('Оружие выдано; аналитика ещё доставляется', ex.COMPLETED),
    rejected=('Сохранённый отказ', ex.REJECTED), review=('Нужна проверка оператора', ex.REVIEW))
READ_MOCK = response(s.MockOrderResponse, 'Оплата и выдача показаны отдельными статусами.',
    pending=('Создан заказ, оплата ещё не подтверждена', ex.MOCK_ORDER),
    fulfilled=('Подтверждён и выдан', ex.MOCK_FULFILLED), refund=('Возврат после выдачи; receipt сохранён', ex.MOCK_REFUNDED))

OPERATIONS = {
    ('/health/live', 'get'): operation('Проверка сервиса', 'Проверить, что процесс отвечает',
        'Не проверяет PostgreSQL, auth или готовность выдачи.', {200: response(s.LiveResponse, 'Процесс работает.', live=('Процесс отвечает', {'status': 'live'}))}),
    ('/health/ready', 'get'): operation('Проверка сервиса', 'Проверить готовность БД и миграций',
        'Сверяет ревизию PostgreSQL с ожидаемой версией shop. Не является проверкой всех внешних сервисов.',
        {200: response(s.ReadyResponse, 'Схема готова.', ready=('Миграции применены', {'status': 'ready', 'schemaRevision': SCHEMA_REVISION}))}),
    ('/v1/shop', 'get'): operation('Каталог', 'Получить каталог для текущего игрока',
        'Скопируйте version в catalogVersion покупки и offers[].id в offerId. Доступность и лимиты рассчитаны для владельца токена.',
        {200: response(s.ShopCatalogResponse, 'Каталог, версия, определения и доступность.', catalog=('Пример доступного товара; версия иллюстративна', ex.CATALOG))}),
    ('/v1/purchases', 'post'): operation('Покупки за престиж', 'Купить оружие за престиж',
        'Сохраните operationId до первого запроса. Повторите исходное тело после таймаута. Новый UUID означает новую покупку. '
        '202 — принята обработка; 200 — прежняя операция Completed/Review; 409 — сохранённый Rejected либо конфликт ключа.',
        {202: response(s.PurchaseReceipt, 'Принято; опрашивайте GET receipt.', pending=('Ожидает worker', ex.PENDING)),
         200: response(s.PurchaseReceipt, 'Результат ранее принятой операции.', completed=('Повтор завершённой операции', {**ex.COMPLETED, 'replayed': True}), review=('Повтор операции Review', {**ex.REVIEW, 'replayed': True})),
         409: response(s.PurchaseReceipt, 'Сохранённый отказ либо HTTP-конфликт; см. обе схемы.', rejected=('Недостаточно престижа', ex.REJECTED))},
        body(buy=('Подставьте версию GET каталога и собственный operationId', ex.PURCHASE_INPUT))),
    ('/v1/purchases/{operation_id}', 'get'): operation('Покупки за престиж', 'Проверить результат покупки',
        'Только операция текущего игрока. Чужой/неизвестный UUID — 404. GET возвращает 200 и для Rejected/Review; проверьте поле status.', {200: READ_RECEIPT}),
    ('/v1/purchases', 'get'): operation('Покупки за престиж', 'Посмотреть историю своих покупок',
        'От новых к старым. Для следующей страницы копируйте nextCursor в before. История другого игрока недоступна.',
        {200: response(s.PurchaseHistory, 'Страница истории.', page=('Последняя страница', {'purchases': [{**ex.COMPLETED, 'createdAt': ex.AT}], 'nextCursor': None}))}),
    ('/v1/admin/shop/offers', 'get'): operation('Администрирование каталога', 'Список товаров для администратора',
        'Возвращает ревизии и tombstones при includeDeleted=true. Курсор after — ID последнего товара.',
        {200: response(s.AdminOffersPage, 'Страница товаров.', page=('Один товар', {'offers': [ex.ADMIN_OFFER], 'nextCursor': None}))}),
    ('/v1/admin/shop/offers/{offer_id}', 'get'): operation('Администрирование каталога', 'Прочитать товар и его ревизию',
        'Прочитайте revision перед изменением или удалением; удалённая запись также доступна для аудита.',
        {200: response(s.AdminOffer, 'Карточка товара.', offer=('Товар без картинки', ex.ADMIN_OFFER))}),
    ('/v1/admin/shop/offers', 'post'): operation('Администрирование каталога', 'Добавить товар за престиж',
        'Нужна действующая конфигурация оружия game_backend. ID уникален навсегда, включая удалённые товары. '
        'Это каталог за престиж; внешние TEST-продукты создаются отдельным MOCK API.',
        {201: response(s.AdminOffer, 'Создано.', created=('Новый товар, revision=1', ex.ADMIN_OFFER))},
        body(create=('Товар без картинки', ex.OFFER_INPUT), withImage=('Сначала загрузите картинку и подставьте её настоящий imageKey', {**ex.OFFER_INPUT, 'imageKey': ex.IMAGE_KEY}))),
    ('/v1/admin/shop/offers/{offer_id}', 'put'): operation('Администрирование каталога', 'Изменить товар с проверкой ревизии',
        'Передайте все обязательные поля и expectedRevision из GET. Это PUT: пропущенные необязательные поля получают значения по умолчанию, '
        'кроме imageKey — отсутствие сохраняет прежнюю картинку, null очищает её. Уже принятая покупка сохраняет исходную цену/награду.',
        {200: response(s.AdminOffer, 'Изменено или возвращено текущее состояние при совпадении содержимого.', updated=('Новая цена', {**ex.ADMIN_OFFER, 'price': 30, 'revision': 2}))},
        body(update=('Изменить цену и сохранить картинку', {**ex.OFFER_UPDATE, 'price': 30}), clearImage=('Очистить картинку', {**ex.OFFER_UPDATE, 'imageKey': None}))),
    ('/v1/admin/shop/offers/{offer_id}', 'delete'): operation('Администрирование каталога', 'Удалить товар из витрины',
        'Требуется expectedRevision из GET. Ответ без тела; сохраняется tombstone, ID не освобождается. Принятые операции продолжаются.',
        {204: {'description': 'Удалено; тело ответа отсутствует.'}}),
    ('/v1/admin/shop/images', 'post'): operation('Администрирование каталога', 'Загрузить картинку товара в S3',
        'Отправьте исходные байты PNG/JPEG, не JSON и не multipart. В Swagger выберите файл в Request body и соответствующий Content-Type. '
        'До 8 MiB, 4096×4096, один кадр. Скопируйте imageKey результата в создание/правку товара. Повтор тех же байтов даёт тот же ключ.',
        {201: response(s.UploadedImage, 'Объект сохранён; imageUrl доступен для чтения.', image=('Пример ссылки; подставьте настоящий imageKey ответа', ex.IMAGE))},
        {'required': True, 'description': 'Сам файл без multipart-обёртки.', 'content': {mime: {'schema': {'type': 'string', 'format': 'binary'}} for mime in ('image/png', 'image/jpeg')}}),
    ('/v1/admin/shop/purchases', 'get'): operation('Восстановление покупок', 'Найти операции для разбора',
        'Фильтры по status и accountId. before — непрозрачный nextCursor из ответа, не UUID operationId.',
        {200: response(s.AdminPurchasesPage, 'Страница операций.', review=('Операция в Review', {'purchases': [ex.ADMIN_SUMMARY], 'nextCursor': None}))}),
    ('/v1/admin/shop/accounts/{account_id}/purchases/{operation_id}', 'get'): operation('Восстановление покупок', 'Проверить план и шаг восстановления',
        'Доступно только ADMIN. canRetry и job.version используются перед retry. Поле plan — исходный серверный снимок.',
        {200: response(s.AdminPurchaseDetail, 'Карточка для сверки.', review=('Выдача требует проверки', ex.ADMIN_DETAIL))}),
    ('/v1/admin/shop/accounts/{account_id}/purchases/{operation_id}/events', 'get'): operation('Восстановление покупок', 'Прочитать журнал переходов покупки',
        'События по sequence по возрастанию. Передайте nextCursor как after. Автор/причина заполнены для административных действий.',
        {200: response(s.PurchaseEventsPage, 'Журнал.', events=('Пример перехода', {'events': [ex.EVENT], 'nextCursor': None}))}),
    ('/v1/admin/shop/accounts/{account_id}/purchases/{operation_id}/retry', 'post'): operation('Восстановление покупок', 'Продолжить покупку после сверки',
        'Только восстанавливаемый Review. Сначала сверяйте внешние результаты и прочитайте job.version. '
        'Устаревшая версия или изменённое тело с прежним requestKey — 409; завершённые шаги не открываются заново.',
        {200: response(s.PurchaseRetryResponse, 'Возвращена в Pending; результат выдачи проверяйте отдельно.', retry=('Повтор запланирован', ex.RETRY_RESULT))},
        body(retry=('Подставьте текущую job.version и причину', ex.RETRY_INPUT))),
    ('/v1/mock/products', 'get'): operation('MOCK: игрок', 'Получить TEST-каталог продуктов',
        'Включает отключённые продукты с enabled=false; новый заказ по ним запрещён. Цена и награда заданы сервером.',
        {200: response(s.MockProductsPage, 'Страница TEST-продуктов.', page=('Один опубликованный продукт', {'products': [{key: value for key, value in ex.MOCK_PRODUCT.items() if key != 'replayed'}], 'nextCursor': None}))}),
    ('/v1/admin/shop/mock/products', 'post'): operation('MOCK: администратор', 'Опубликовать неизменяемый MOCK-продукт',
        'Нужны ADMIN и MockControlKey. rewardBundleId должен быть опубликован в game_backend: только WEAPON, до 20 единиц. '
        'SKU/version и externalSku неизменяемы. Для новой цены/награды создайте новую версию с новым externalSku.',
        {201: response(s.MockProductResponse, 'Опубликовано.', created=('Продукт создан', ex.MOCK_PRODUCT)),
         200: response(s.MockProductResponse, 'Идентичная версия уже существует.', replay=('Повтор публикации', {**ex.MOCK_PRODUCT, 'replayed': True}))},
        body(publish=('Подставьте UUID своего опубликованного набора', ex.MOCK_PRODUCT_INPUT))),
    ('/v1/admin/shop/mock/products/{identity}/availability', 'patch'): operation('MOCK: администратор', 'Включить или отключить новые TEST-заказы',
        'Отключение не отменяет уже принятые заказы. Повторяйте прежний requestKey и enabled после таймаута.',
        {200: response(s.MockAvailabilityResponse, 'Доступность установлена.', disabled=('Новые покупки закрыты', {'productId': ex.PRODUCT_ID, 'enabled': False, 'replayed': False}))},
        body(disable=('Отключить продукт', {'enabled': False, 'requestKey': ex.REQUEST}))),
    ('/v1/mock/orders', 'post'): operation('MOCK: игрок', 'Создать идемпотентный TEST-заказ',
        'Передайте только productId и requestKey. Не передавайте цену или аккаунт. Повтор того же ключа возвращает исходный заказ; '
        'другой productId с тем же ключом — 409. Новый ключ — новая consumable-покупка. HTTP 202 не означает оплату.',
        {202: response(s.MockOrderResponse, 'Заказ принят или возвращён при повторе.', created=('Новый заказ', ex.MOCK_ORDER), replay=('Повтор уже выполненного заказа', {**ex.MOCK_FULFILLED, 'replayed': True}))},
        body(create=('Подставьте productId из TEST-каталога', ex.MOCK_CREATE))),
    ('/v1/mock/orders/{identity}', 'get'): operation('MOCK: игрок', 'Проверить оплату и выдачу TEST-заказа',
        'Только владелец. VERIFIED — оплата проверена, FULFILLED — выдача завершена. UNKNOWN — lookup провайдера не подтверждён. '
        'units[].receipt показывает фактическую выдачу. REFUNDED/REVIEW после dispatch не означает изъятие оружия.', {200: READ_MOCK}),
    ('/v1/mock/orders/{identity}/confirm', 'post'): operation('MOCK: игрок', 'Передать SDK receipt для сверки',
        'Скопируйте receipt из реального simulate. Проверяются владелец/заказ/SKU/сумма/валюта/TEST. '
        'Receipt только ставит сверку; worker проверяет запись провайдера независимо. Повтор не выдаёт награду заново.',
        {202: response(s.MockOrderResponse, 'Сигнал принят; опрашивайте GET заказа.', accepted=('Сверка запланирована', ex.MOCK_ORDER))},
        body(confirm=('Замените обозначение реальным receipt из simulate', {'receipt': '<receipt из ответа simulate>'}))),
    ('/v1/admin/shop/mock/orders/{identity}', 'get'): operation('MOCK: администратор', 'Проверить чужой TEST-заказ для восстановления',
        'ADMIN + MockControlKey. Показывает владельца, сохранённый продукт, receipts и version для retry.', {200: READ_MOCK}),
    ('/v1/admin/shop/mock/orders/{identity}/simulate', 'post'): operation('MOCK: администратор', 'Выполнить сценарий TEST-провайдера',
        'Нужны ADMIN и MockControlKey. Возвращает SDK receipt и webhook, но сам webhook не отправляет. '
        'Повтор requestKey возвращает прежний ответ; для нового сценария нужен новый ключ. '
        'DECLINED/REFUNDED нельзя вернуть в SUCCESS, VERIFIED нельзя вернуть в PENDING. providerUnavailable отключает lookup.',
        {200: response(s.MockSimulationResponse, 'Результат симулятора; receipt/signature в примерах обозначения, не действующие значения.',
            success=('SUCCESS — используйте настоящие receipt/signature ответа', ex.SIMULATED),
            timeout=('Оплата прошла, SDK-ответ потерян', {**ex.SIMULATED, 'scenario': 'TIMEOUT', 'sdkStatus': 'TIMEOUT', 'receipt': None}))},
        body(**{name.lower(): (name, {'requestKey': ex.REQUEST, 'scenario': name, 'providerUnavailable': False})
                for name in ('SUCCESS', 'DECLINED', 'PENDING', 'TIMEOUT', 'REFUND', 'DUPLICATE_WEBHOOK')},
             outage=('Провайдер подтвердил, но его lookup недоступен', {'requestKey': ex.REQUEST, 'scenario': 'SUCCESS', 'providerUnavailable': True}))),
    ('/v1/admin/shop/mock/orders/{identity}/retry', 'post'): operation('MOCK: администратор', 'Возобновить TEST-заказ после Review',
        'Нужны ADMIN + MockControlKey и актуальная version из GET. Укажите reason после сверки зависимостей. '
        'Снова проверяется провайдер; завершённые единицы не повторяются. REFUNDED/DECLINED не открываются заново.',
        {200: response(s.MockRetryResponse, 'Возобновлена сверка.', retry=('Восстановление запланировано', {'orderId': ex.ORDER, 'version': 10, 'replayed': False, 'reason': 'Провайдер восстановлен; заказ сверен'}))},
        body(retry=('Сначала прочитайте текущую version', {'expectedVersion': 9, 'requestKey': ex.REQUEST, 'reason': 'Провайдер восстановлен; заказ сверен'}))),
    ('/v1/mock/provider/webhook', 'post'): operation('MOCK: webhook', 'Принять подписанный сигнал TEST-провайдера',
        'До 16 KiB исходного JSON. X-Mock-Signature — lowercase hex HMAC-SHA256 точных байтов тела с webhook-ключом, '
        'не MockControlKey. JSON из simulate сериализуется с sort_keys=True, separators=(",", ":"), ensure_ascii=True. '
        'Try it out может менять пробелы: тогда подпись надо пересчитать TEST-инструментом. '
        'Повтор eventId безопасен, изменённое содержимое того же события конфликтует. Приём только ставит сверку.',
        {200: response(s.MockWebhookAccepted, 'Сигнал записан, но оплата/выдача ещё не гарантированы.',
            accepted=('Первое событие', {'eventId': ex.WEBHOOK['eventId'], 'accepted': True, 'replayed': False}),
            replay=('Повтор того же события', {'eventId': ex.WEBHOOK['eventId'], 'accepted': True, 'replayed': True}))},
        body(event=('Структура; используйте body и подпись своего simulate', ex.WEBHOOK))),
}

PARAMETERS = {
    'limit': ('Размер страницы. Обычно 20 для истории игрока, 50 для административных/MOCK списков; максимум 100.', 20),
    'operation_id': ('UUID операции покупки, сохранённый до POST /v1/purchases.', ex.OPERATION),
    'account_id': ('UUID владельца покупки, не текущего администратора.', ex.ACCOUNT),
    'offer_id': ('Стабильный ID предложения из каталога.', 'sword'),
    'includeDeleted': ('Показать также удалённые товары с deletedAt.', False),
    'accountId': ('Фильтр по владельцу операции.', ex.ACCOUNT),
    'status': ('Статус покупки за престиж; регистр важен.', 'Review'),
    'expectedRevision': ('Текущая revision товара из GET; защищает от конкурентной правки.', 1),
}

# Application errors differ by endpoint. Database conflicts/outages are included
# for operations which use a repository; liveness has no database dependency.
ERROR_STATUSES = {
    ('/health/live', 'get'): (500,),
    ('/health/ready', 'get'): (409, 500, 503),
    ('/v1/shop', 'get'): (401, 409, 500, 503),
    ('/v1/purchases', 'post'): (401, 409, 422, 500, 503),
    ('/v1/purchases/{operation_id}', 'get'): (401, 404, 409, 422, 500, 503),
    ('/v1/purchases', 'get'): (401, 404, 409, 422, 500, 503),
    ('/v1/admin/shop/offers', 'get'): (401, 403, 409, 422, 500, 503),
    ('/v1/admin/shop/offers/{offer_id}', 'get'): (401, 403, 404, 409, 422, 500, 503),
    ('/v1/admin/shop/offers', 'post'): (401, 403, 409, 422, 500, 503),
    ('/v1/admin/shop/offers/{offer_id}', 'put'): (401, 403, 404, 409, 422, 500, 503),
    ('/v1/admin/shop/offers/{offer_id}', 'delete'): (401, 403, 404, 409, 422, 500, 503),
    ('/v1/admin/shop/images', 'post'): (401, 403, 413, 422, 500, 503),
    ('/v1/admin/shop/purchases', 'get'): (401, 403, 409, 422, 500, 503),
    ('/v1/admin/shop/accounts/{account_id}/purchases/{operation_id}', 'get'): (401, 403, 404, 409, 422, 500, 503),
    ('/v1/admin/shop/accounts/{account_id}/purchases/{operation_id}/events', 'get'): (401, 403, 404, 409, 422, 500, 503),
    ('/v1/admin/shop/accounts/{account_id}/purchases/{operation_id}/retry', 'post'): (401, 403, 404, 409, 422, 500, 503),
    ('/v1/mock/products', 'get'): (401, 409, 422, 500, 503),
    ('/v1/admin/shop/mock/products', 'post'): (401, 403, 409, 422, 500, 503),
    ('/v1/admin/shop/mock/products/{identity}/availability', 'patch'): (401, 403, 404, 409, 422, 500, 503),
    ('/v1/mock/orders', 'post'): (401, 404, 409, 422, 500, 503),
    ('/v1/mock/orders/{identity}', 'get'): (401, 404, 409, 422, 500, 503),
    ('/v1/mock/orders/{identity}/confirm', 'post'): (401, 404, 409, 422, 500, 503),
    ('/v1/admin/shop/mock/orders/{identity}', 'get'): (401, 403, 404, 409, 422, 500, 503),
    ('/v1/admin/shop/mock/orders/{identity}/simulate', 'post'): (401, 403, 404, 409, 422, 500, 503),
    ('/v1/admin/shop/mock/orders/{identity}/retry', 'post'): (401, 403, 404, 409, 422, 500, 503),
    ('/v1/mock/provider/webhook', 'post'): (401, 404, 409, 413, 422, 500, 503),
}


def install_documentation(app, settings):
    """Decorate only the specification; handlers retain their original response bodies."""
    app.title = 'VaticanDefendor — магазин'
    app.description = INTRO + (MOCK_INTRO if settings.mock_payments_enabled else
        '\n## MOCK выключен\nTEST/dev endpoints скрыты, включая OpenAPI. Для отдельного TEST-стенда см. backend/docs/shop/MOCK_PAYMENTS.ru.md.\n')
    app.openapi_tags = [tag for tag in TAGS if settings.mock_payments_enabled or not tag['name'].startswith('MOCK:')]
    app.swagger_ui_parameters = {'docExpansion': 'none', 'filter': True, 'displayRequestDuration': True,
                                 'persistAuthorization': False, 'defaultModelsExpandDepth': 0}

    def openapi():
        if app.openapi_schema is not None:
            return app.openapi_schema
        spec = get_openapi(title=app.title, version=app.version, description=app.description,
                           routes=app.routes, tags=app.openapi_tags)
        schemas = spec.setdefault('components', {}).setdefault('schemas', {})
        models = {s.ShopError}
        for path, methods in spec['paths'].items():
            for method in methods:
                metadata = OPERATIONS[(path, method)]
                for result in metadata['responses'].values():
                    if 'model' in result:
                        models.add(result['model'])
        _, definitions = models_json_schema([(model, 'serialization') for model in sorted(models, key=lambda value: value.__name__)],
                                            ref_template='#/components/schemas/{model}')
        schemas.update(definitions['$defs'])
        spec['components']['securitySchemes'] = {
            'PlayerBearer': {'type': 'http', 'scheme': 'bearer', 'bearerFormat': 'opaque auth token',
                'description': 'Токен auth без префикса Bearer. ADMIN endpoints дополнительно проверяют текущую роль негостевого аккаунта.'}}
        if settings.mock_payments_enabled:
            spec['components']['securitySchemes'].update({
                'MockControlKey': {'type': 'apiKey', 'in': 'header', 'name': 'X-Mock-Control-Key',
                    'description': 'Отдельный серверный TEST control key. Используется вместе с токеном ADMIN; не является webhook-подписью.'},
                'MockWebhookSignature': {'type': 'apiKey', 'in': 'header', 'name': 'X-Mock-Signature',
                    'description': 'HMAC-SHA256 именно отправляемых байтов webhook, а не постоянный ключ. Берите из simulate или вычисляйте TEST-инструментом.'}})
        for path, methods in spec['paths'].items():
            for method, op in methods.items():
                data = OPERATIONS[(path, method)]
                op.update(summary=data['summary'], description=data['description'], tags=[data['tag']])
                op['security'] = [] if path.startswith('/health/') else [{'PlayerBearer': []}]
                if '/admin/shop/mock/' in path:
                    op['security'] = [{'PlayerBearer': [], 'MockControlKey': []}]
                if path == '/v1/mock/provider/webhook':
                    op['security'] = [{'MockWebhookSignature': []}]
                op['parameters'] = [p for p in op.get('parameters', []) if p.get('in') != 'header']
                for parameter in op['parameters']:
                    name = parameter['name']
                    if name in PARAMETERS:
                        parameter['description'], parameter['example'] = PARAMETERS[name]
                    elif name == 'identity':
                        parameter.update(description='UUID продукта из TEST-каталога.' if '/products/' in path else 'UUID TEST-заказа из create.',
                                         example=ex.PRODUCT_ID if '/products/' in path else ex.ORDER)
                    elif name in ('after', 'before'):
                        parameter['description'] = ('Номер последнего события; 0 начинает журнал.' if path.endswith('/events') else
                            'Курсор nextCursor предыдущей страницы; первый запрос без курсора. Формат зависит от списка, не конструируйте вручную.')
                if data['request'] is not None:
                    content = deepcopy(data['request'])
                    if path.endswith('/images'):
                        op['requestBody'] = content
                    else:
                        request = op.setdefault('requestBody', {'required': True, 'content': {}})
                        for mime, value in content['content'].items():
                            request['content'].setdefault(mime, {}).update(value)
                        if path == '/v1/mock/provider/webhook':
                            request['content']['application/json']['schema'] = {'$ref': '#/components/schemas/MockWebhookBody'}
                op['responses'] = {}
                for status, result in data['responses'].items():
                    value = deepcopy({key: value for key, value in result.items() if key != 'model'})
                    if 'model' in result:
                        value['content']['application/json']['schema'] = {'$ref': '#/components/schemas/' + result['model'].__name__}
                    op['responses'][str(status)] = value
                for status in ERROR_STATUSES[(path, method)]:
                    error = error_response(status, path, method, op['parameters'])
                    if str(status) in op['responses']:
                        success = op['responses'][str(status)]['content']['application/json']
                        success['schema'] = {'oneOf': [success['schema'], error['content']['application/json']['schema']]}
                        success['examples'].update(error['content']['application/json']['examples'])
                    else:
                        op['responses'][str(status)] = error
                for status, result in op['responses'].items():
                    result.setdefault('headers', {})['X-Request-ID'] = {'description': 'UUID запроса; совпадает с requestId в ошибке.',
                        'schema': {'type': 'string', 'format': 'uuid'}, 'example': ex.REQUEST}
                    if status == '503':
                        result['headers']['Retry-After'] = {'description': 'Минимальная пауза перед повтором с прежним ключом.', 'schema': {'type': 'string'}, 'example': '1'}
                    if status == '401':
                        result['headers']['WWW-Authenticate'] = {'description': 'Заголовок общего обработчика 401; нужные учётные данные указаны в security метода.',
                            'schema': {'type': 'string'}, 'example': 'Bearer'}
        schemas.pop('HTTPValidationError', None)
        schemas.pop('ValidationError', None)
        app.openapi_schema = spec
        return spec

    app.openapi = openapi


def error_response(status, path, method, parameters):
    values = {
        401: ('Нужны действующие учётные данные для этого метода.', 'InvalidCredentials', 'Bearer credential required'),
        403: ('Нужна текущая роль ADMIN у негостевого аккаунта.', 'PermissionDenied', 'Administrator role required'),
        404: ('Объект не найден, недоступен либо принадлежит другому игроку.', 'ResourceNotFound', 'Purchase operation not found'),
        409: ('Конфликт ключа, версии или состояния; прочитайте актуальное состояние перед новым решением.', 'ResourceConflict', 'Purchase operation payload changed'),
        413: ('Превышен лимит тела: изображение 8 MiB, webhook 16 KiB.', 'PayloadTooLarge', 'MOCK webhook is too large'),
        422: ('Проверьте поля, UUID и допустимые значения; подробности могут быть в errors.', 'InvalidRequest', 'Request validation failed'),
        500: ('Внутренняя ошибка; сохраните requestId для диагностики.', 'InternalError', 'An internal error occurred'),
        503: ('Недоступна БД, миграции или зависимость. Повторяйте прежнюю операцию с учётом Retry-After.', 'DatabaseUnavailable', 'Shop database is temporarily unavailable'),
    }
    description, code, detail = values[status]
    if status == 401 and path == '/v1/mock/provider/webhook':
        description, detail = 'Неверная или отсутствующая подпись исходных байтов webhook.', 'Invalid MOCK webhook signature'
    if status == 413 and path.endswith('/images'):
        description, detail = 'Исходное изображение больше 8 MiB.', 'Image exceeds 8 MiB'
    if status == 404 and '/mock/' in path:
        detail = 'MOCK product not found' if '/products/' in path else 'MOCK order not found'
    if status == 409 and '/mock/' in path:
        detail = 'MOCK command payload changed' if '/admin/' in path else 'MOCK order request payload changed'
    if status == 409:
        conflicts = {
            ('/v1/admin/shop/offers', 'post'): 'Shop offer ID already exists',
            ('/v1/admin/shop/offers/{offer_id}', 'put'): 'Shop offer revision changed',
            ('/v1/admin/shop/offers/{offer_id}', 'delete'): 'Shop offer revision changed',
            ('/v1/admin/shop/accounts/{account_id}/purchases/{operation_id}/retry', 'post'): 'Purchase state or version changed',
            ('/v1/admin/shop/mock/products', 'post'): 'MOCK product version is immutable',
            ('/v1/admin/shop/mock/orders/{identity}/retry', 'post'): 'MOCK order cannot be retried at this version',
            ('/v1/mock/provider/webhook', 'post'): 'MOCK event payload changed',
        }
        detail = conflicts.get((path, method), detail)
        if method == 'get':
            code, detail = 'ConcurrentModification', 'Another operation conflicted with this request; retry the operation'
            if path == '/v1/mock/products':
                code, detail = 'ResourceConflict', 'Invalid MOCK product routing'
    value = {'code': code, 'detail': detail, 'requestId': ex.REQUEST, 'errors': []}
    if status == 422:
        if method in ('post', 'put', 'patch'):
            value['errors'] = [{'field': 'body', 'type': 'missing', 'message': 'Required field is missing'}]
        elif any(parameter['name'] == 'limit' for parameter in parameters):
            value['errors'] = [{'field': 'query.limit', 'type': 'less_than_equal', 'message': 'Value is outside the allowed range'}]
    examples = {'httpError': ex.example(description, value)}
    if status == 409 and code != 'ConcurrentModification':
        examples['concurrentModification'] = ex.example('Конфликт транзакций БД; повторите ту же операцию',
            {**value, 'code': 'ConcurrentModification', 'detail': 'Another operation conflicted with this request; retry the operation'})
    if status == 401 and '/admin/shop/mock/' in path:
        examples['controlKey'] = ex.example('Токен ADMIN верен, но отдельный control key не принят',
            {**value, 'detail': 'Separate MOCK control credential required'})
    return {'description': description, 'content': {'application/json': {
        'schema': {'$ref': '#/components/schemas/ShopError'}, 'examples': examples}}}
