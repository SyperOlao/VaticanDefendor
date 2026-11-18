"""OpenAPI response contracts; do not filter or rewrite persisted runtime receipts."""
from datetime import datetime
from typing import Literal
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field


class DocumentedResponse(BaseModel):
    model_config = ConfigDict(extra='forbid')


class ValidationIssue(DocumentedResponse):
    field: str = Field(description='Путь к полю, например body.offerId; без исходного значения.')
    type: str = Field(description='Безопасный код нарушения формата.')
    message: str = Field(description='Пояснение ошибки поля на английском.')


class ShopError(DocumentedResponse):
    code: str = Field(description='Код HTTP-ошибки для обработки клиентом.')
    detail: str = Field(description='Безопасное описание; может отличаться от текста примера.')
    requestId: UUID = Field(description='Идентификатор для диагностики; совпадает с X-Request-ID ответа.')
    errors: list[ValidationIssue] = Field(description='Ошибки отдельных полей; обычно пустой список для бизнес-конфликтов.')


class LiveResponse(DocumentedResponse):
    status: Literal['live']


class ReadyResponse(DocumentedResponse):
    status: Literal['ready']
    schemaRevision: str = Field(description='Применённая ревизия миграции shop.')


class CatalogOffer(DocumentedResponse):
    id: str = Field(description='ID предложения; передаётся как offerId при покупке.')
    weaponConfigurationId: str = Field(description='ID определения оружия в game_backend.')
    title: str
    price: int = Field(ge=0, description='Цена в единицах престижа, не в рублях или центах.')
    purchaseLimit: int = Field(ge=0, description='Лимит на аккаунт; 0 означает без ограничения.')
    currencyId: Literal['prestige']
    available: bool
    unavailableReason: str = Field(description='Пустая строка при доступности; иначе код причины, например PurchaseLimitReached.')
    imageKey: str | None = None
    imageUrl: str | None = Field(default=None, description='Публичная ссылка картинки S3; null, если картинка не привязана.')
    purchasedCount: int = Field(ge=0, description='Число покупок, учитываемых в лимите текущего игрока, включая зарезервированные операции.')


class ShopCatalogResponse(DocumentedResponse):
    version: str = Field(description='Версия всего снимка. Копируйте в catalogVersion без изменений.')
    currencyId: Literal['prestige']
    offers: list[CatalogOffer]
    configurations: list[dict] = Field(description='Определения оружия game_backend; структура определяется игровым контентом.')
    revision: int | None = Field(default=None, description='Ревизия каталога БД; может отсутствовать у исторического источника.')


class AnalyticsDelivery(DocumentedResponse):
    status: Literal['SKIPPED', 'PENDING', 'COMPLETED', 'REVIEW']
    messageId: UUID | None
    attempts: int | None = None
    lastError: str | None = None
    receipt: dict | None = None


class PurchaseReceipt(DocumentedResponse):
    operationId: UUID
    offerId: str
    status: Literal['Pending', 'Completed', 'Rejected', 'Review'] = Field(description='Результат покупки; не путать с доставкой аналитики.')
    replayed: bool = Field(description='true при повторном POST с уже сохранённым operationId.')
    code: str | None = Field(default=None, description='Причина отказа/Review, например InsufficientFunds или ParticipantConflict.')
    chargedAmount: int | None = Field(default=None, ge=0)
    currencyId: Literal['PRESTIGE'] | None = None
    grantedWeaponInstanceIds: list[UUID] | None = None
    inventoryRevision: int | None = None
    completedAt: datetime | None = Field(default=None, description='Время подтверждённого завершения покупки; может отсутствовать у исторического receipt.')
    analyticsStatus: Literal['SKIPPED', 'PENDING', 'COMPLETED', 'REVIEW'] | None = None
    analytics: AnalyticsDelivery | None = None


class HistoricalPurchase(PurchaseReceipt):
    createdAt: datetime


class PurchaseHistory(DocumentedResponse):
    purchases: list[HistoricalPurchase]
    nextCursor: UUID | None = Field(description='Для следующей страницы передайте как before. null — конец списка.')


class AdminOffer(DocumentedResponse):
    id: str
    weaponConfigurationId: str
    title: str
    price: int = Field(ge=0)
    purchaseLimit: int = Field(ge=0)
    currencyId: Literal['prestige']
    available: bool
    unavailableReason: str
    imageKey: str | None
    imageUrl: str | None
    revision: int = Field(ge=1, description='Копируйте в expectedRevision при правке/удалении.')
    createdAt: datetime
    updatedAt: datetime
    deletedAt: datetime | None = Field(description='Tombstone: удалённый ID нельзя использовать для нового товара.')


class AdminOffersPage(DocumentedResponse):
    offers: list[AdminOffer]
    nextCursor: str | None = Field(description='Передайте как after; ID последнего предложения страницы.')


class UploadedImage(DocumentedResponse):
    imageKey: str = Field(description='Скопируйте в imageKey предложения. Путь содержит SHA-256 изображения.')
    imageUrl: str
    contentType: Literal['image/png', 'image/jpeg']
    sizeBytes: int = Field(ge=1)
    width: int = Field(ge=1, le=4096)
    height: int = Field(ge=1, le=4096)


class PurchaseJobInfo(DocumentedResponse):
    state: Literal['PENDING', 'CHARGED', 'REFUND_PENDING', 'COMPLETED', 'REJECTED', 'REVIEW']
    version: int = Field(description='Копируйте в expectedVersion; версия изменяется при обработке worker.')
    attempts: int
    lastErrorCode: str | None
    leaseExpiresAt: datetime | None
    nextAttemptAt: datetime | None


class AdminPurchaseSummary(DocumentedResponse):
    accountId: UUID
    operationId: UUID
    offerId: str
    createdAt: datetime
    status: Literal['Pending', 'Completed', 'Rejected', 'Review']
    job: PurchaseJobInfo | None
    canRetry: bool = Field(description='Операция находится в Review и допускает административное восстановление.')


class AdminPurchaseDetail(AdminPurchaseSummary):
    catalogVersion: str
    receipt: PurchaseReceipt
    plan: dict | None = Field(description='Исходная серверная цена и configurationId. Администратор не меняет этот план при retry.')
    resumeState: str | None


class AdminPurchasesPage(DocumentedResponse):
    purchases: list[AdminPurchaseSummary]
    nextCursor: str | None = Field(description='Непрозрачный курсор; передайте как before без декодирования и правок.')


class PurchaseEventResponse(DocumentedResponse):
    sequence: int
    kind: str
    fromState: str | None
    state: str
    attempts: int | None
    errorCode: str | None
    actorId: UUID | None
    requestKey: UUID | None
    reason: str | None
    createdAt: datetime


class PurchaseEventsPage(DocumentedResponse):
    events: list[PurchaseEventResponse]
    nextCursor: int | None = Field(description='Номер последнего события; следующая страница через after.')


class PurchaseRetryResponse(DocumentedResponse):
    accountId: UUID
    operationId: UUID
    status: Literal['Pending']
    jobVersion: int
    replayed: bool


class MockRewardLine(DocumentedResponse):
    itemIndex: int = Field(ge=0)
    rewardType: Literal['WEAPON']
    rewardRef: str
    quantity: int = Field(ge=1, le=20)


class MockRewardSnapshot(DocumentedResponse):
    schemaVersion: Literal[1]
    bundleId: UUID
    items: list[MockRewardLine]


class MockProductSnapshot(DocumentedResponse):
    sku: str
    version: int
    externalSku: str
    rewardBundleId: UUID
    priceMinor: int = Field(ge=0, description='Условная TEST-цена в minor units: 199 USD означает 1.99 USD; денег не списывает.')
    currencyIso: str
    productId: UUID
    rewardSnapshot: MockRewardSnapshot


class MockProductResponse(MockProductSnapshot):
    enabled: bool
    replayed: bool | None = None


class MockProductsPage(DocumentedResponse):
    products: list[MockProductResponse]
    nextCursor: UUID | None = Field(description='Передайте как after; null — конец каталога.')


class MockGrantReceipt(DocumentedResponse):
    accountId: UUID
    transactionId: UUID
    lineIndex: int
    unitIndex: int
    configurationId: str
    status: Literal['APPLIED']
    instanceId: UUID
    inventoryRevision: int


class MockUnitResponse(DocumentedResponse):
    lineIndex: int
    unitIndex: int
    state: Literal['PENDING', 'COMPLETED', 'REVIEW', 'CANCELLED']
    attempts: int
    lastError: str | None
    receipt: MockGrantReceipt | None


class MockOrderResponse(DocumentedResponse):
    orderId: UUID
    accountId: UUID
    environment: Literal['TEST']
    status: Literal['PENDING', 'UNKNOWN', 'VERIFIED', 'DECLINED', 'FULFILLED', 'REFUNDED']
    fulfillmentStatus: Literal['NOT_READY', 'PENDING', 'PARTIAL', 'COMPLETED', 'CANCELLED', 'REVIEW']
    version: int = Field(description='Текущая версия для административного retry, не версия продукта.')
    transactionId: UUID | None
    providerVersion: int
    reviewCode: str | None
    createdAt: datetime
    product: MockProductSnapshot
    units: list[MockUnitResponse]
    replayed: bool


class MockAvailabilityResponse(DocumentedResponse):
    productId: UUID
    enabled: bool
    replayed: bool


class MockWebhookBody(DocumentedResponse):
    orderId: UUID
    eventId: UUID
    environment: Literal['TEST']
    providerVersion: int


class SignedMockWebhook(DocumentedResponse):
    body: MockWebhookBody
    signature: str = Field(pattern=r'^[a-f0-9]{64}$', description='64 lowercase hex символа HMAC-SHA256 от точных байтов канонического JSON body. Используйте подпись реального ответа simulate.')


class MockSimulationResponse(DocumentedResponse):
    orderId: UUID
    scenario: Literal['SUCCESS', 'DECLINED', 'PENDING', 'TIMEOUT', 'REFUND', 'DUPLICATE_WEBHOOK']
    replayed: bool
    sdkStatus: Literal['PENDING', 'VERIFIED', 'DECLINED', 'REFUNDED', 'TIMEOUT']
    receipt: str | None = Field(description='Непрозрачный подписанный SDK receipt. При TIMEOUT null; не генерировать на клиенте.')
    webhook: SignedMockWebhook


class MockWebhookAccepted(DocumentedResponse):
    eventId: UUID
    accepted: Literal[True]
    replayed: bool


class MockRetryResponse(DocumentedResponse):
    orderId: UUID
    version: int
    replayed: bool
    reason: str
