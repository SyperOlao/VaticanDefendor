"""OpenAPI response descriptions; runtime responses remain owned by the use cases."""
from datetime import datetime
from typing import Any,Literal
from uuid import UUID
from pydantic import BaseModel,ConfigDict,Field


class DocumentedResponse(BaseModel):
    model_config=ConfigDict(extra='allow')


class ValidationIssue(BaseModel):
    field: str = Field(description='Поле запроса, например body.expectedLevel.')
    type: str = Field(description='Машинный тип ошибки проверки.')
    message: str = Field(description='Безопасное пояснение ошибки.')


class ErrorResponse(BaseModel):
    code: str = Field(description='Стабильный код для обработки приложением; текст detail может меняться.')
    detail: str = Field(description='Пояснение причины отказа.')
    requestId: UUID = Field(description='ID запроса для поиска в логах; также возвращается в X-Request-ID.')
    errors: list[ValidationIssue] = Field(description='Ошибки полей при 422; в остальных случаях обычно пустой список.')


class ProfileResponse(BaseModel):
    accountId: UUID = Field(description='Аккаунт из токена auth.')
    version: int = Field(description='Текущая версия. Передайте её как expectedVersion при следующем редактировании.')
    displayName: str = Field(description='Отображаемый ник после нормализации.')
    avatarAssetKey: str|None = Field(description='Ключ из каталога аватаров; null — аватар не выбран.')
    displayTimezoneIana: str = Field(description='Часовой пояс отображения; серверные timestamps остаются UTC.')
    locale: str = Field(description='Язык интерфейса.')


class PlayerDisplayName(BaseModel):
    accountId: UUID
    displayName: str


class PlayerDisplayNames(BaseModel):
    players: list[PlayerDisplayName]


class AvatarOption(BaseModel):
    assetKey: str
    title: str
    symbol: str
    backgroundColor: str = Field(description='Цвет фона в формате #RRGGBB.')


class AvatarCatalog(BaseModel):
    avatars: list[AvatarOption]


class EnergyResponse(BaseModel):
    accountId: UUID
    rulesetId: str
    currentEnergy: int = Field(description='Доступная энергия с учётом восстановления на момент calculatedAt.')
    maxEnergy: int = Field(description='Предел естественного восстановления. Купленная энергия может превышать его.')
    runCost: int = Field(description='Стоимость нового забега; выбирается сервером.')
    regenIntervalMs: int
    energyPerTick: int
    regenProgressMs: int = Field(description='Сохранённая часть следующего тика восстановления.')
    regenFractionMs: str = Field(description='Дробная часть миллисекунды без потери точности.')
    stateVersion: int
    calculatedAt: datetime


class XPReceipt(DocumentedResponse):
    rulesetId: str
    xpGranted: int = Field(description='XP только за этот забег; именно эта дельта идёт в рейтинг.')
    totalXp: int = Field(description='Общий XP игрока после исходного начисления, не обязательно текущий при повторе.')
    previousLevel: int
    currentLevel: int
    breakdown: dict[str,int] = Field(description='heroKillXp, towerKillXp, tycoonXp, victoryXp.')
    unlockedRewards: list[dict[str,Any]] = Field(description='Открытые подарки. Содержимое нужно получить отдельным claim-запросом.')


class DeliveryResponse(DocumentedResponse):
    status: Literal['SKIPPED','PENDING','COMPLETED','REVIEW'] = Field(description='COMPLETED — получен и сохранён проверенный чек внешнего сервиса.')
    messageId: UUID|None
    attempts: int|None = None
    lastError: str|None = None
    receipt: dict[str,Any]|None = Field(default=None,description='Сохранённое подтверждение. Исторические значения в нём не меняются при повторе.')


class SettlementReceipt(BaseModel):
    runId: UUID
    outcome: Literal['victory','defeat']
    permanentReward: int = Field(description='Запланированный престиж; факт доставки смотрите в rewardStatus.')
    profileVersion: int
    progression: XPReceipt|None


class RunStatusResponse(DocumentedResponse):
    runId: UUID
    status: Literal['pending','ready','failed','completed']|None = Field(default=None,description='Для готового dedicated сервер возвращает поля подключения без status. Для LOCAL — отдельная структура LocalRunResponse.')
    assignmentExpiresAt: datetime|None = None
    serverId: UUID|None = None
    host: str|None = None
    port: int|None = None
    transport: str|None = None
    certificateName: str|None = None
    contentReleaseId: str|None = None
    protocolVersion: int|None = None
    profileVersion: int|None = None
    profileDisplayName: str|None = None
    grant: str|None = Field(default=None,description='Короткоживущий допуск к Unity-серверу. Это не accessToken auth.')
    grantExpiresAt: int|None = Field(default=None,description='Истечение допуска: Unix timestamp, секунды.')
    outcome: Literal['victory','defeat']|None = None
    permanentReward: int|None = None
    progression: XPReceipt|None = None
    rewardStatus: Literal['PENDING','COMPLETED','FAILED']|None = None
    ratingStatus: Literal['SKIPPED','PENDING','COMPLETED','REVIEW']|None = None
    analyticsStatus: Literal['SKIPPED','PENDING','COMPLETED','REVIEW']|None = None
    rating: DeliveryResponse|None = None
    analytics: DeliveryResponse|None = None
    replayed: bool|None = None


class LocalRunResponse(DocumentedResponse):
    runId: UUID
    accountId: UUID
    simulationMode: Literal['LOCAL']
    status: Literal['pending','ready','failed','completed']
    replayed: bool
    contentReleaseId: str
    rulesetId: str
    profileVersion: int = Field(description='Версия профиля, закреплённая при старте. Версия после начисления — в settlement.profileVersion.')
    displayName: str
    weapons: dict[str,Any] = Field(description='Закреплённые определения, экземпляры и loadout inventory.')
    prestigeBonuses: dict[str,Any] = Field(description='Закреплённые modifiers с multiplier. Покупка во время забега влияет только на следующий старт.')
    sessionId: UUID|None
    policyId: str|None
    mapId: str|None
    difficulty: str|None
    startedAt: datetime|None
    submissionDeadline: datetime|None = Field(description='Сдать новую сводку нужно строго до этого времени UTC.')
    energyCost: int|None
    submissionSchemaVersion: int
    validatorVersion: str
    validationStatus: Literal['ACCEPTED','REJECTED']|None
    validationReasons: list[str]
    settlementStatus: Literal['SKIPPED','PENDING','COMPLETED','REJECTED']
    settlement: SettlementReceipt|None
    rewardStatus: Literal['SKIPPED','PENDING','COMPLETED','FAILED']
    ratingStatus: Literal['SKIPPED','PENDING','COMPLETED','REVIEW']
    rating: DeliveryResponse
    analyticsStatus: Literal['SKIPPED','PENDING','COMPLETED','REVIEW']
    analytics: DeliveryResponse


class ActiveLocalResponse(BaseModel):
    active: bool
    run: LocalRunResponse|None


class ProgressionResponse(BaseModel):
    accountId: UUID
    totalXp: int
    currentLevel: int
    rulesetId: str|None
    nextLevelTotalXp: int|None
    availableRewards: list[dict[str,Any]]


class RewardItem(DocumentedResponse):
    itemIndex: int
    rewardType: Literal['CURRENCY','WEAPON','ITEM','CASE','ENERGY','EFFECT']
    rewardRef: str
    quantity: int
    effect: dict[str,Any]|None = Field(default=None,description='Для EFFECT: modifierValue — decimal-строка, durationSeconds — длительность.')


class RewardBundleResponse(BaseModel):
    schemaVersion: int
    bundleId: UUID
    items: list[RewardItem]


class LevelRewardResponse(DocumentedResponse):
    claimId: UUID
    rewardedLevel: int
    rulesetId: str
    rewardBundleId: UUID
    status: Literal['AVAILABLE','PENDING','CLAIMED','REVIEW']
    deliveryStatus: str = Field(description='PARTIAL — часть строк уже выдана; повтор не выдаёт их заново.')
    requiresReview: bool
    requestedAt: datetime|None
    claimedAt: datetime|None
    lines: list[dict[str,Any]] = Field(description='Строки выдачи со status, messageId и receipt для каждой награды.')
    replayed: bool
    analyticsStatus: Literal['SKIPPED','PENDING','COMPLETED','REVIEW']
    analytics: DeliveryResponse


class LevelRewardList(BaseModel):
    claims: list[dict[str,Any]]
    limit: int
    offset: int


class CaseCatalog(BaseModel):
    cases: list[dict[str,Any]]
    limit: int
    offset: int


class CaseOpeningResponse(DocumentedResponse):
    openingId: UUID
    caseId: UUID
    caseCode: str
    requestKey: UUID
    paymentMode: Literal['INVENTORY','WALLET']
    status: Literal['FUNDING','PENDING','COMPLETED','REJECTED','REVIEW']
    deliveryStatus: str
    requiresReview: bool
    paymentMessageId: UUID|None
    paymentReceipt: dict[str,Any]|None
    result: dict[str,Any]|None = Field(description='Сохранённый розыгрыш; при повторе не выполняется заново.')
    lines: list[dict[str,Any]]
    completedAt: datetime|None
    replayed: bool
    analyticsStatus: Literal['SKIPPED','PENDING','COMPLETED','REVIEW']
    analytics: DeliveryResponse


class PrestigeLevel(BaseModel):
    level: int
    cost: int = Field(description='Цена перехода на этот уровень в PRESTIGE.')
    modifierValue: str = Field(description='Суммарная доля бонуса этого уровня: 0.100000 означает +10%, multiplier=1.1. Предыдущие уровни повторно не суммируются.')


class PrestigeUpgrade(DocumentedResponse):
    code: str
    target: Literal['HERO','ARCHER','WALL']
    stat: Literal['MOVE_SPEED','ATTACK_SPEED','HEALTH','ATTACK_DAMAGE']
    levels: list[PrestigeLevel]
    currentLevel: int
    maxLevel: int
    requiresReview: bool
    nextLevel: PrestigeLevel|None


class PrestigePurchaseResponse(BaseModel):
    purchaseId: UUID
    requestKey: UUID
    rulesetId: str
    code: str
    fromLevel: int
    toLevel: int
    cost: int
    currencyId: Literal['PRESTIGE']
    status: Literal['PENDING','COMPLETED','REJECTED','REVIEW']
    paymentMessageId: UUID
    attempts: int
    lastError: str|None
    paymentReceipt: dict[str,Any]|None = Field(description='В чеке code — код улучшения; причина отказа — rejectionCode.')
    result: dict[str,Any]|None = Field(description='Купленный уровень, bonus и profileVersion после применения.')
    completedAt: datetime|None
    replayed: bool


class PrestigeCatalog(BaseModel):
    rulesetId: str|None
    available: bool = Field(description='false — оператор ещё не выбрал опубликованную конфигурацию.')
    upgrades: list[PrestigeUpgrade]
    bonuses: dict[str,Any]
    legacyUpgrades: list[dict[str,Any]]
    requiresReview: bool
    pendingPurchase: PrestigePurchaseResponse|None


class ContentResponse(BaseModel):
    releaseId: str
    schemaVersion: int
    payloadJson: str = Field(description='Исходная строка JSON. SHA-256 считается по её точным байтам UTF-8, до повторной сериализации.')
    payloadSha256: str


class WeaponConfigurationResponse(DocumentedResponse):
    Id: str
    Name: str
    Rarity: int
    Damage: float
    AttackInterval: float
    MaximumLevel: int
    ExperiencePerLevel: int
    IconUrl: str|None = None
    ImageUrl: str|None = None


class HealthResponse(BaseModel):
    status: str
    schemaVersion: str|None = None


class Acknowledgement(DocumentedResponse):
    status: str|None = None


class ServerAssignmentResponse(DocumentedResponse):
    assigned: bool
    recoveryPending: bool|None = None
    runId: UUID|None = None
    accountId: UUID|None = None
    contentReleaseId: str|None = None
    rulesetId: str|None = None
    profileVersion: int|None = None
    displayName: str|None = None
    generation: int|None = None
    weaponsJson: str|None = None
    prestigeJson: str|None = None
    snapshotJson: str|None = None
    checkpointAgeSeconds: float|None = None
    recoveryDisconnectedSeconds: float|None = None
    assignmentExpiresAt: datetime|None = None
