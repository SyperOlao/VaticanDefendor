"""Documentation-only models; domain validation and HTTP serialization stay unchanged."""
from datetime import datetime
from typing import Annotated, Literal
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field

MAX = 9_223_372_036_854_775_807
Count = Annotated[int, Field(strict=True, ge=0)]
Amount = Annotated[int, Field(strict=True, ge=0, le=MAX)]
Level = Annotated[int, Field(strict=True, ge=1, le=2_147_483_647)]


class Documented(BaseModel):
    model_config = ConfigDict(extra='forbid')


class Stats(Documented):
    heroKills: dict[str, Annotated[int, Field(ge=0, le=2_147_483_647)]] = Field(description='Количество убийств героем по кодам врагов. До 256 кодов; пустой объект означает отсутствие убийств.')
    towerKills: dict[str, Annotated[int, Field(ge=0, le=2_147_483_647)]] = Field(description='Убийства башнями по кодам врагов. Значения — количества, не опыт.')
    buildingUpgrades: dict[str, Annotated[int, Field(ge=0, le=2_147_483_647)]] = Field(description='Количество улучшений по кодам построек. До 256 кодов.')


class ExperienceBreakdown(Documented):
    heroKillXp: Amount = Field(description='Опыт за убийства героем.')
    towerKillXp: Amount = Field(description='Опыт за убийства башнями.')
    tycoonXp: Amount = Field(description='Опыт за улучшения построек.')
    victoryXp: Amount = Field(description='Опыт за победу; 0 при отсутствии этой награды.')


class Progression(Documented):
    xpGranted: Amount = Field(description='Опыт, начисленный за этот забег. Равен сумме четырёх компонентов breakdown.')
    totalXp: Amount = Field(description='Общий опыт после начисления; не меньше xpGranted.')
    previousLevel: Level = Field(description='Уровень до забега.')
    currentLevel: Level = Field(description='Уровень после забега; не ниже previousLevel.')
    breakdown: ExperienceBreakdown = Field(description='Все четыре компонента обязательны, даже если равны нулю.')


class RunPayload(Documented):
    runId: UUID = Field(description='Постоянный ID завершённого забега. Один и тот же при повторной доставке.')
    accountId: UUID = Field(description='ID игрока из auth; в ClickHouse сохраняется как player_id.')
    rulesetId: str = Field(pattern=r'^[A-Za-z0-9_.-]{1,128}$', description='Версия правил, по которым завершён забег.')
    contentReleaseId: str = Field(pattern=r'^[A-Za-z0-9_.-]{1,128}$', description='Версия опубликованного игрового контента.')
    completedAt: datetime = Field(description='Время завершения с UTC-часовым поясом: Z или +00:00. Год 1970–2200.')
    outcome: Literal['victory','defeat'] = Field(description='victory — победа, defeat — поражение.')
    churchLevel: Annotated[int, Field(strict=True,ge=1,le=5)] = Field(description='Уровень церкви при завершении забега.')
    stats: Stats = Field(description='Подтверждённые счётчики действий.')
    progression: Progression | None = Field(description='Начисление опыта; null означает, что сведений нет. Нулевой опыт передавайте объектом с xpGranted=0.')
    matchResultSnapshot: dict | None = Field(default=None, description='Опциональная серверная статистика матча schemaVersion=1: длительность, волны, урон, постройки и ресурсы. Сохраняется целиком в raw_game_events.payload_json; не рассчитывает награды.')
    simulationMode: Literal['LOCAL','DEDICATED'] = Field(default='DEDICATED',description='LOCAL — локальная симуляция, DEDICATED — выделенный сервер. Поле можно не передавать.')


class GiftPayload(Documented):
    claimId: UUID = Field(description='Постоянный ID получения подарка, ключ повторной доставки.')
    accountId: UUID = Field(description='ID получателя.')
    rulesetId: str = Field(pattern=r'^[A-Za-z0-9_.-]{1,128}$',description='Версия правил выдачи.')
    rewardBundleId: UUID = Field(description='ID набора наград; не ID отдельного предмета.')
    rewardedLevel: Level = Field(description='Уровень, за достижение которого выдан подарок.')
    claimedAt: datetime = Field(description='Время подтверждённого получения в UTC, год 1970–2200.')
    status: Literal['CLAIMED'] = Field(description='Только полученные подарки. Ожидающие выдачи не принимаются этим событием.')


class CasePayload(Documented):
    openingId: UUID = Field(description='Постоянный ID открытия кейса.')
    accountId: UUID = Field(description='ID игрока.')
    rulesetId: str = Field(pattern=r'^[A-Za-z0-9_.-]{1,128}$',description='Версия правил открытия.')
    rewardBundleId: UUID = Field(description='ID выбранного набора наград.')
    caseId: UUID = Field(description='ID определения кейса.')
    caseCode: str = Field(pattern=r'^[A-Za-z0-9_.-]{1,64}$',description='Код кейса из каталога.')
    paymentMode: Literal['INVENTORY','WALLET'] = Field(description='INVENTORY — кейс из инвентаря, WALLET — оплата валютой. Суммы списания в этом событии нет.')
    rarity: str = Field(pattern=r'^[A-Za-z0-9_.-]{1,32}$',description='Код редкости выбранной награды, например RARE.')
    pityBefore: Annotated[int,Field(strict=True,ge=0,le=2_147_483_647)] = Field(description='Счётчик гаранта до открытия; не число оставшихся открытий.')
    pityAfter: Annotated[int,Field(strict=True,ge=0,le=2_147_483_647)] = Field(description='0 после сброса либо pityBefore + 1.')
    guaranteedTriggered: bool = Field(description='Сработал ли гарант. При true pityAfter должен быть 0.')
    entryIndex: Annotated[int,Field(strict=True,ge=0,le=2_147_483_647)] = Field(description='Индекс выбранной записи награды, начиная с 0.')
    completedAt: datetime = Field(description='Время завершения оплаты и выдачи, UTC, год 1970–2200.')
    status: Literal['COMPLETED'] = Field(description='Только полностью завершённые открытия.')


class PurchasePayload(Documented):
    purchaseId: UUID = Field(description='Детерминированный ID: UUIDv5(accountId, "shop.purchase:" + operationId). Возьмите готовый ID из shop; произвольный UUID не подойдёт.')
    accountId: UUID = Field(description='ID покупателя.')
    operationId: UUID = Field(description='ID операции покупки, общий для её повторов.')
    offerId: str = Field(pattern=r'^[a-z0-9_.-]{1,64}$',description='Код предложения магазина.')
    catalogVersion: str = Field(pattern=r'^[A-Za-z0-9_.-]{1,64}$',description='Версия каталога в момент покупки.')
    currencyId: Literal['PRESTIGE'] = Field(description='В этом контракте принимается только внутриигровой престиж.')
    chargedAmount: Amount = Field(description='Фактически списанная сумма; 0 — бесплатная выдача.')
    weaponConfigurationId: str = Field(pattern=r'^[A-Za-z0-9_.-]{1,80}$',description='Код определения выданного оружия.')
    weaponInstanceIds: Annotated[list[UUID],Field(min_length=1,max_length=1)] = Field(description='Ровно один ненулевой UUID выданного экземпляра оружия.')
    status: Literal['Completed'] = Field(description='Успех магазина. Регистр отличается от COMPLETED в событии кейса.')
    completedAt: datetime = Field(description='Время завершения покупки в UTC, год 1970–2200.')


class WalletPayload(Documented):
    transactionId: UUID = Field(description='ID строки журнала кошелька; ключ повторной доставки.')
    accountId: UUID = Field(description='ID владельца кошелька.')
    currencyId: str = Field(pattern=r'^[A-Za-z0-9_.:-]{1,32}$',description='Код валюты, например PRESTIGE или GEMS.')
    delta: Annotated[int,Field(strict=True,ge=-MAX-1,le=MAX)] = Field(description='Изменение баланса: плюс — начисление, минус — списание. Ноль не допускается.')
    balanceAfter: Amount = Field(description='Баланс после операции. balanceAfter − delta также должен быть в диапазоне 0…2^63−1.')
    reason: str = Field(min_length=1,max_length=64,description='Причина операции из кошелька, например PURCHASE.')
    sourceType: str = Field(pattern=r'^[A-Za-z0-9_.:-]{1,32}$',description='Тип источника: PURCHASE, RUN_REWARD, REFUND и другие коды кошелька.')
    sourceId: UUID = Field(description='ID исходной покупки, забега, подарка или иной операции.')
    itemIndex: Annotated[int,Field(strict=True,ge=0,le=2_147_483_647)] = Field(description='Индекс позиции награды; 0 для одиночной операции.')
    occurredAt: datetime = Field(description='Время операции в UTC, год 1970–2200.')


class Envelope(Documented):
    messageId: UUID = Field(description='ID доставки. Для повтора того же сообщения сохраняйте UUID и всё тело.')
    schemaVersion: Literal[1] = Field(description='Поддерживается версия 1. Передавайте число, не строку и не true.')


class RunMessage(Envelope):
    eventName: Literal['run.analytics']
    payload: RunPayload

class GiftMessage(Envelope):
    eventName: Literal['level-gift.analytics']
    payload: GiftPayload

class CaseMessage(Envelope):
    eventName: Literal['case.analytics']
    payload: CasePayload

class PurchaseMessage(Envelope):
    eventName: Literal['shop.purchase.analytics']
    payload: PurchasePayload

class WalletMessage(Envelope):
    eventName: Literal['wallet.transaction.analytics']
    payload: WalletPayload

AnalyticsMessage = Annotated[RunMessage|GiftMessage|CaseMessage|PurchaseMessage|WalletMessage,Field(discriminator='eventName')]


class Receipt(Documented):
    accountId: UUID = Field(description='Владелец исходной операции.')
    payloadHash: str = Field(pattern=r'^[0-9a-f]{64}$',description='SHA-256 принятого содержимого; позволяет проверить неизменность повторов.')
    eventIds: list[UUID] = Field(description='Стабильные ID записанных аналитических событий.')
    runId: UUID | None = None
    claimId: UUID | None = None
    openingId: UUID | None = None
    purchaseId: UUID | None = None
    transactionId: UUID | None = None


class DeliveryResult(Documented):
    messageId: UUID = Field(description='ID сообщения из запроса.')
    status: Literal['PENDING','APPLIED','REVIEW'] = Field(description='PENDING — принято; APPLIED — запись подтверждена; REVIEW — требуется разбор сбоя.')
    receipt: Receipt | None = Field(description='Квитанция после успешной записи. До подтверждения — null.')


class SourceStatus(Documented):
    accountId: UUID = Field(description='ID владельца исходной операции.')
    runId: UUID | None = Field(default=None,description='Присутствует у run.analytics.')
    claimId: UUID | None = Field(default=None,description='Присутствует у level-gift.analytics.')
    openingId: UUID | None = Field(default=None,description='Присутствует у case.analytics.')
    purchaseId: UUID | None = Field(default=None,description='Присутствует у shop.purchase.analytics.')
    transactionId: UUID | None = Field(default=None,description='Присутствует у wallet.transaction.analytics.')
    sourceId: UUID = Field(description='ID исходной операции из пути запроса.')
    eventName: str = Field(description='Тип аналитического контракта.')
    status: Literal['PENDING','COMPLETED','REVIEW'] = Field(description='Состояние фоновой проекции; COMPLETED соответствует APPLIED в приёме сообщений.')
    attempts: Count = Field(description='Число попыток фоновой записи.')
    lastError: str | None = Field(description='Безопасный код последнего сбоя или null.')
    receipt: Receipt | None = Field(description='Квитанция успешно записанного источника или null.')
    completedAt: datetime | None = Field(description='Время подтверждения записи в UTC либо null.')


class ValidationIssue(Documented):
    field: str = Field(description='Безопасный путь поля; может быть сокращён до body/query/path.')
    type: str = Field(description='Машинный тип нарушения.')
    message: str = Field(description='Безопасное пояснение без исходного значения.')


class ApiError(Documented):
    code: str = Field(description='Машинный код причины, например InvalidRequest или DatabaseUnavailable.')
    detail: str = Field(description='Краткая причина. Значения сохраняют язык реального HTTP-контракта — английский.')
    requestId: UUID = Field(description='Код обращения; тот же ID в заголовке X-Request-ID.')
    errors: list[ValidationIssue] = Field(description='Нарушения полей; пустой список для ошибок без полей.')
