from uuid import UUID
from datetime import datetime
from typing import Literal
from pydantic import BaseModel, ConfigDict, Field
from ..domain.broker import InboxStatus, EventStatus, Destination
from ..application.catalog import EventName
from . import examples as ex


class Message(BaseModel):
    model_config = ConfigDict(extra="forbid")
    messageId: UUID = Field(description="UUID доставки. При повторной отправке той же команды сохраняйте его и всё тело.", examples=[ex.MESSAGE])
    eventName: str = Field(min_length=1, max_length=128, description="Имя команды из таблицы в описании метода.", examples=["level.reward"], json_schema_extra={"enum": list(EventName)})
    schemaVersion: int = Field(ge=1, strict=True, description="Версия контракта команды. Все реализованные команды используют целое число 1.", examples=[1], json_schema_extra={"enum": [1]})
    payload: dict = Field(description="Тело выбранной команды. Полные варианты доступны в списке Examples у запроса.", examples=[ex.LEVEL_PAYLOAD])


class Loadout(BaseModel):
    model_config = ConfigDict(extra="forbid")
    InstanceIds: list[UUID] = Field(min_length=1, max_length=3, description="От 1 до 3 различных UUID принадлежащего игроку оружия. Порядок массива задаёт порядок слотов.", examples=[[ex.WEAPON]])
    expectedRevision: int | None = Field(default=None, ge=1, le=9_223_372_036_854_775_807, strict=True,
        description="Ревизия из последнего чтения инвентаря. При несовпадении — 409; получите состояние заново перед новым выбором.", examples=[7])
    requestKey: UUID | None = Field(default=None, description="UUID операции выбора. Сохраните для повторов; для нового выбора создайте новый UUID. Повтор возвращает исходный ответ и не меняет более новый выбор.", examples=[ex.OPERATION])


class LoadoutResult(BaseModel):
    requestKey: UUID = Field(description="Ключ сохранённой операции.")
    status: Literal["APPLIED"] = Field(description="Выбор был применён или уже совпадал с сохранённым.")
    InstanceIds: list[UUID] = Field(description="Выбранные этой операцией экземпляры в порядке слотов.")
    inventoryRevision: int = Field(ge=1, description="Ревизия в момент выполнения операции; текущая может быть выше.")


class Grant(BaseModel):
    model_config = ConfigDict(extra="forbid")
    accountId: UUID = Field(description="UUID игрока из auth, которому выдаётся оружие.", examples=[ex.ACCOUNT])
    operationId: UUID = Field(description="Стабильный UUID покупки. Вместе с accountId защищает выдачу от повторов HTTP/RabbitMQ.", examples=[ex.OPERATION])
    configurationId: str = Field(pattern=r"^[A-Za-z0-9_.-]{1,80}$", description="ID определения оружия из game_backend; это не UUID экземпляра.", examples=["sword.common"])


class OwnedWeapon(BaseModel):
    InstanceId: UUID = Field(description="UUID конкретного экземпляра. Используется при экипировке.", examples=[ex.WEAPON])
    ConfigurationId: str = Field(description="ID определения оружия; несколько экземпляров могут иметь одинаковый ID.", examples=["sword.common"])
    Level: int = Field(ge=1, description="Текущий уровень экземпляра.", examples=[2])
    Experience: int = Field(ge=0, description="Накопленный опыт экземпляра, без сброса при повышении уровня.", examples=[100])


class Weapons(BaseModel):
    Configurations: list[dict] = Field(description="Определения из game_backend. Минимальные поля: Id, ExperiencePerLevel, MaximumLevel; дополнительные поля контента сохраняются.", examples=[[ex.DEFINITION]])
    Weapons: list[OwnedWeapon] = Field(description="Принадлежащие игроку экземпляры; во внутреннем snapshot — только выбранные.")
    Loadout: list[UUID] = Field(description="UUID выбранных экземпляров в порядке слотов, от 1 до 3.", examples=[[ex.WEAPON]])
    Revision: int = Field(ge=1, description="Ревизия инвентаря. Передавайте как expectedRevision при изменении экипировки.", examples=[7])
    loadoutOperation: LoadoutResult | None = Field(default=None, description="Подтверждение выбора, если PUT выполнялся с requestKey. В обычном GET поле отсутствует.")


class InventoryItem(BaseModel):
    itemId: str = Field(description="Ключ предмета item:<ref> или кейса case:<code>. Старые ключи возвращаются без переименования.", examples=["item:repair-kit"])
    quantity: int = Field(ge=0, description="Текущий остаток. Строка с нулём сохраняется после списания.", examples=[4])


class Inventory(BaseModel):
    items: list[InventoryItem] = Field(description="Предметы и кейсы; при пагинации — только текущая страница.")
    revision: int = Field(ge=1, description="Ревизия состояния, из которого прочитана страница.", examples=[7])
    nextCursor: str | None = Field(default=None, description="Передайте в query cursor с прежними фильтрами. null — последняя страница. Без параметров пагинации поле отсутствует.")


class OwnedWeaponsPage(BaseModel):
    Weapons: list[OwnedWeapon] = Field(description="Экземпляры текущей страницы без определений game_backend.")
    Loadout: list[UUID] = Field(description="Весь текущий loadout; часть его экземпляров может находиться на других страницах.")
    Revision: int = Field(ge=1, description="Ревизия страницы. При её изменении продолжение обхода вернёт 409.", examples=[7])
    nextCursor: str | None = Field(description="Курсор следующей страницы либо null в конце обхода.")


class ItemDetail(InventoryItem):
    revision: int = Field(ge=1, description="Текущая ревизия инвентаря.", examples=[7])


class WeaponDetail(BaseModel):
    Weapon: OwnedWeapon = Field(description="Принадлежащий текущему игроку экземпляр.")
    Configuration: dict = Field(description="Его текущее определение из game_backend.", examples=[ex.DEFINITION])
    Revision: int = Field(ge=1, description="Текущая ревизия инвентаря.", examples=[7])


class Consumption(BaseModel):
    model_config = ConfigDict(extra="forbid")
    accountId: UUID = Field(description="UUID игрока из auth.", examples=[ex.ACCOUNT])
    operationId: UUID = Field(description="Стабильный UUID списания, общий для HTTP и RabbitMQ. Новый предмет/количество требуют нового UUID.", examples=[ex.OPERATION])
    itemId: str = Field(min_length=1, max_length=160, description="Ключ обычного предмета, включая legacy. Кейсы case:* здесь запрещены: используйте команду case.consume.", examples=["item:repair-kit"])
    quantity: int = Field(ge=1, le=9_223_372_036_854_775_807, strict=True, description="Сколько единиц списать. Только положительное целое число.", examples=[2])


class ConsumptionResult(Consumption):
    status: Literal["APPLIED", "REJECTED"] = Field(description="APPLIED — списано; REJECTED — сохранён отказ InsufficientItems. Оба результата возвращаются с HTTP 200.")
    quantityAfter: int = Field(ge=0, description="Остаток на момент исходной операции; повтор возвращает его же.")
    inventoryRevision: int | None = Field(default=None, ge=1, description="Ревизия после успешного списания. В ответе отказа отсутствует.")
    code: str | None = Field(default=None, description="Причина бизнес-отказа: InsufficientItems. При успехе поле отсутствует.")


class GrantResult(Grant):
    status: Literal["APPLIED", "REJECTED"] = Field(description="APPLIED — оружие выдано; REJECTED — сохранён отказ UnknownWeaponConfiguration. Оба результата возвращаются с HTTP 200.")
    instanceId: UUID | None = Field(default=None, description="UUID выданного экземпляра. Повтор не создаёт новый экземпляр.")
    inventoryRevision: int | None = Field(default=None, ge=1, description="Ревизия, подтверждающая выдачу; актуальное состояние может иметь более высокую.")
    code: str | None = Field(default=None, description="Причина отказа, если status=REJECTED.")


class MockGrant(BaseModel):
    model_config = ConfigDict(extra='forbid')
    accountId: UUID = Field(description="Ненулевой UUID получателя из auth.", examples=[ex.ACCOUNT])
    transactionId: UUID = Field(description="Ненулевой UUID проверенной TEST-транзакции. Источник выдачи — transactionId/lineIndex/unitIndex.", examples=[ex.SOURCE])
    lineIndex: int = Field(strict=True, ge=0, le=2_147_483_647, description="Индекс строки тестового набора, начиная с 0.", examples=[0])
    unitIndex: int = Field(strict=True, ge=0, le=2_147_483_647, description="Индекс экземпляра внутри строки, начиная с 0. Один запрос выдаёт одно оружие.", examples=[0])
    configurationId: str = Field(pattern=r'^[A-Za-z0-9_.-]{1,80}$', description="ID существующего определения оружия в game_backend.", examples=['sword.common'])


class MockGrantResult(MockGrant):
    status: Literal['APPLIED'] = Field(description="Экземпляр выдан либо возвращено исходное подтверждение этого источника.")
    instanceId: UUID = Field(description="UUID выданного экземпляра, неизменный при повторе.")
    inventoryRevision: int = Field(ge=1, description="Ревизия инвентаря после исходной выдачи.")


class ValidationIssue(BaseModel):
    field: str = Field(description="Безопасный путь к ошибочному полю.", examples=["body.quantity"])
    type: str = Field(description="Машинный тип ошибки проверки.", examples=["greater_than_equal"])
    message: str = Field(description="Краткое описание ошибки без исходного значения.", examples=["Value is outside the allowed range"])


class ApiError(BaseModel):
    code: str = Field(description="Машинный код ошибки: InvalidRequest, ResourceConflict и другие.", examples=["ResourceConflict"])
    detail: str = Field(description="Пояснение причины на английском языке.", examples=["Inventory revision changed"])
    requestId: UUID = Field(description="ID запроса для поиска в логах. Возвращается также в X-Request-ID.", examples=[ex.OPERATION])
    errors: list[ValidationIssue] = Field(description="Ошибки отдельных полей для 422; для остальных ошибок обычно пустой массив.")


class Liveness(BaseModel):
    status: Literal['live'] = Field(description="HTTP-процесс работает; зависимости здесь не проверяются.")


class Readiness(BaseModel):
    status: Literal['ready'] = Field(description="PostgreSQL доступна, миграции соответствуют версии приложения.")
    schemaRevision: str = Field(description="Применённая ревизия inventory.")


class MessageResult(BaseModel):
    messageId: UUID = Field(description="ID обработанной доставки.")
    status: Literal["APPLIED"] = Field(description="Команда обработана. Бизнес-результат проверяйте внутри receipt.")
    receipt: dict = Field(description="Сохранённый результат источника: status у выдачи покупки/списания предмета, outcome у case.consume; у награды — данные выдачи.")


class BrokerEventBrief(BaseModel):
    eventId: UUID = Field(description="Стабильный UUID события outbox; получатель дедуплицирует его перед ACK.")
    destination: Destination = Field(description="Логическое направление. rabbit.quarantine публикуется в dead exchange.")
    attempts: int = Field(ge=0, description="Число попыток публикации.")
    status: EventStatus = Field(description="PENDING — ожидается/повторяется; PUBLISHED — получен publisher confirm; REVIEW — требуется оператор после исчерпания попыток.")


class BrokerEventSummary(BrokerEventBrief):
    commandOrRejectionId: UUID = Field(description="messageId исходной команды либо rejectionId карантина.")
    eventName: str = Field(description="inventory.command.result или inventory.command.dead.")
    lastError: str | None = Field(description="Безопасный код последней ошибки; null, если её нет.")
    createdAt: datetime = Field(description="Время создания события в UTC.")
    availableAt: datetime = Field(description="Время следующей попытки; при leased=true — срок аренды.")
    leased: bool = Field(description="Есть сохранённый токен аренды. После availableAt аренда может быть получена другим worker.")


class BrokerCommandSummary(BaseModel):
    messageId: UUID = Field(description="UUID принятой RabbitMQ-команды.")
    status: InboxStatus = Field(description="PENDING — ждёт исполнения/повтора; APPLIED — обработана; REJECTED — ошибка команды; REVIEW — исчерпаны попытки, нужен оператор.")
    attempts: int = Field(ge=0, description="Число попыток исполнения в текущем поколении.")
    deliveryGeneration: int = Field(ge=1, description="Поколение исполнения. Увеличивается при операторском replay команды.")
    lastError: str | None = Field(description="Безопасный код ошибки без исходного payload.")
    createdAt: datetime = Field(description="Время первого приёма команды.")
    availableAt: datetime = Field(description="Время следующей попытки либо срок текущей аренды.")
    leased: bool = Field(description="Наличие сохранённого токена аренды; срок см. availableAt.")


class BrokerCommandStatus(BaseModel):
    messageId: UUID = Field(description="UUID принятой команды. HTTP-only доставка не создаёт broker inbox.")
    payloadSha256: str = Field(description="SHA-256 канонического исходного envelope. Отправитель сверяет его перед подтверждением бизнес-операции.")
    status: InboxStatus = Field(description="Состояние исполнения команды, отдельно от доставки событий.")
    attempts: int = Field(ge=0, description="Число попыток исполнения.")
    deliveryGeneration: int = Field(ge=1, description="Поколение, увеличиваемое операторским replay.")
    lastError: str | None = Field(description="Последний безопасный код ошибки.")
    result: dict | None = Field(description="null до завершения; затем исходный ответ сценария либо отказ REJECTED/REVIEW с code. APPLIED не заменяет проверку receipt.")
    events: list[BrokerEventBrief] = Field(description="Публикации всех поколений. APPLIED команды и PUBLISHED события — разные этапы.")


class BrokerRejectionSummary(BaseModel):
    rejectionId: UUID = Field(description="ID карантина, доступный даже для тела без корректного messageId.")
    messageId: UUID | None = Field(description="ID конфликтующей команды, если был определён; иначе null.")
    code: str = Field(description="Безопасная причина: InvalidEnvelope, MessageIdMismatch, MessageConflict или PayloadTooLarge.")
    bodySha256: str = Field(description="SHA-256 исходного тела, 64 шестнадцатеричных символа.")
    bodySize: int = Field(ge=0, description="Полный размер исходного тела в байтах.")
    bodyTruncated: bool = Field(description="В карантине сохранён только префикс тела (не более 4096 байт). Само тело в API не выводится.")
    createdAt: datetime = Field(description="Время сохранения карантина.")
    event: BrokerEventSummary | None = Field(description="Связанная публикация dead-letter; её eventId используется в CLI replay_broker --outbox.")


class BrokerCommandsPage(BaseModel):
    commands: list[BrokerCommandSummary] = Field(description="Команды от новых к старым по времени создания и UUID.")
    nextCursor: str | None = Field(description="Следующая страница с тем же фильтром status; null — конец.")


class BrokerEventsPage(BaseModel):
    events: list[BrokerEventSummary] = Field(description="Публикации результатов и dead-letter от новых к старым.")
    nextCursor: str | None = Field(description="Следующая страница с тем же фильтром status; null — конец.")


class BrokerRejectionsPage(BaseModel):
    rejections: list[BrokerRejectionSummary] = Field(description="Карантин от новых к старым, без исходных тел.")
    nextCursor: str | None = Field(description="Следующая страница с тем же фильтром code; null — конец.")
