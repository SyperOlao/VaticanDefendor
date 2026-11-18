from uuid import UUID
from pydantic import BaseModel, ConfigDict, Field


class Purchase(BaseModel):
    model_config = ConfigDict(extra="forbid")
    operationId: UUID = Field(description="Ненулевой UUID, созданный клиентом до запроса. Повторяйте после таймаута; новый UUID — новая покупка.")
    offerId: str = Field(pattern=r"^[a-z0-9_.-]{1,64}$", description="ID предложения из GET /v1/shop, например sword.")
    catalogVersion: str = Field(pattern=r"^[A-Za-z0-9_.-]{1,64}$", min_length=1, max_length=64,
                              description="Точное поле version последнего GET /v1/shop. Версия из примера не заменяет актуальную.")


class Message(BaseModel):
    model_config = ConfigDict(extra="forbid")
    messageId: UUID
    eventName: str = Field(min_length=1, max_length=128)
    schemaVersion: int = Field(ge=1)
    payload: dict


class OfferFields(BaseModel):
    model_config = ConfigDict(extra="forbid")
    weaponConfigurationId: str = Field(pattern=r"^[A-Za-z0-9_.-]{1,80}$", description="Существующее определение оружия в game_backend, например sword.rare.")
    title: str = Field(min_length=1, max_length=80, description="Название для игрока; непустое, без управляющих символов.")
    price: int = Field(strict=True, ge=0, le=9_223_372_036_854_775_807, description="Целая цена в престиже. 0 — бесплатное предложение.")
    purchaseLimit: int = Field(default=0, strict=True, ge=0, le=9_223_372_036_854_775_807, description="Лимит покупок на аккаунт; 0 — без ограничения.")
    available: bool = Field(default=True, strict=True, description="Можно ли принимать новые покупки. Уже принятые продолжаются.")
    unavailableReason: str = Field(default="", max_length=64, description="Пусто для available=true. Иначе буквенно-цифровой код с буквы, например TemporarilyUnavailable.")
    imageKey: str | None = Field(default=None, pattern=r"^shop/items/[a-f0-9]{64}\.(png|jpg)$", max_length=96,
                                description="Ключ из POST /v1/admin/shop/images, не URL. В PUT отсутствие сохраняет картинку, null очищает.")


class OfferCreate(OfferFields):
    id: str = Field(pattern=r"^[a-z0-9_.-]{1,64}$", description="Стабильный ID товара. Не переиспользуется даже после удаления.")


class OfferUpdate(OfferFields):
    expectedRevision: int = Field(strict=True, ge=1, description="revision из актуального GET товара. При конкурентной правке будет 409.")


class RetryPurchase(BaseModel):
    model_config = ConfigDict(extra="forbid")
    expectedVersion: int = Field(strict=True, ge=1, le=9_223_372_036_854_775_807, description="Текущая job.version административной карточки покупки.")
    requestKey: UUID = Field(description="UUID команды администратора. Повторите прежний ключ и тело при потере ответа.")
    reason: str = Field(min_length=1, max_length=500, description="Что оператор сверил и почему безопасно продолжить. Непустой текст для журнала.")
