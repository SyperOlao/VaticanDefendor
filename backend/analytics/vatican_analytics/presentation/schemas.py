from datetime import datetime
from typing import Any, Literal
from uuid import UUID
from pydantic import BaseModel, ConfigDict, Field


class Model(BaseModel):
    model_config = ConfigDict(extra='forbid')


class Liveness(Model):
    status: Literal['live'] = Field(description='HTTP-процесс запущен; доступность БД не проверяется.')


class Readiness(Model):
    status: Literal['ready'] = Field(description='База доступна, ревизия схемы актуальна.')
    schemaRevision: str = Field(description='Текущая начальная ревизия миграций сервиса.')


class ErrorIssue(Model):
    field: str = Field(description='Поле, которое требуется исправить.')
    type: str = Field(description='Машинный код ошибки поля.')
    message: str = Field(description='Безопасное пояснение ошибки.')


class ErrorResponse(Model):
    code: str = Field(description='Код ошибки для клиента.')
    detail: str = Field(description='Безопасное пояснение без SQL и секретов.')
    requestId: UUID = Field(description='UUID запроса; совпадает с X-Request-ID.')
    errors: list[ErrorIssue] = Field(description='Ошибки отдельных полей; может быть пустым.')


class Message(Model):
    messageId: UUID = Field(description='Стабильный UUID доставки. При повторе сохраняйте его и исходное тело.')
    eventName: str = Field(min_length=1, max_length=128, description='Тип серверного события. Поддерживаемые значения перечислены в описании метода.')
    schemaVersion: int = Field(strict=True, ge=1, description='Сейчас поддерживается только версия 1.', json_schema_extra={'const': 1})
    payload: dict[str, Any] = Field(description='Снимок бизнес-источника для выбранного eventName; проверяется use case.')


class ReceiptBase(Model):
    accountId: UUID
    payloadHash: str = Field(description='Хэш исходного снимка, а не всей оболочки сообщения.')
    eventIds: list[UUID] = Field(description='Детерминированные ID событий, записываемых в ClickHouse.')


class RunReceipt(ReceiptBase):
    runId: UUID


class GiftReceipt(ReceiptBase):
    claimId: UUID


class CaseReceipt(ReceiptBase):
    openingId: UUID


class PurchaseReceipt(ReceiptBase):
    purchaseId: UUID


class WalletReceipt(ReceiptBase):
    transactionId: UUID


Receipt = RunReceipt | GiftReceipt | CaseReceipt | PurchaseReceipt | WalletReceipt


class MessageResult(Model):
    messageId: UUID
    status: Literal['PENDING', 'APPLIED', 'REVIEW'] = Field(description='PENDING — источник принят, APPLIED — проекция завершена, REVIEW — исчерпаны попытки.')
    receipt: Receipt | None = Field(description='null до завершения проекции. Только APPLIED с проверенным receipt подтверждает доставку.')


class SourceBase(Model):
    sourceId: UUID = Field(description='UUID бизнес-источника, может отличаться от messageId.')
    accountId: UUID
    status: Literal['PENDING', 'COMPLETED', 'REVIEW'] = Field(description='Состояние проекции: здесь COMPLETED, тогда как POST возвращает APPLIED.')
    attempts: int
    lastError: str | None
    receipt: Receipt | None
    completedAt: datetime | None


class RunSource(SourceBase):
    eventName: Literal['run.analytics']
    runId: UUID


class GiftSource(SourceBase):
    eventName: Literal['level-gift.analytics']
    claimId: UUID


class CaseSource(SourceBase):
    eventName: Literal['case.analytics']
    openingId: UUID


class PurchaseSource(SourceBase):
    eventName: Literal['shop.purchase.analytics']
    purchaseId: UUID


class WalletSource(SourceBase):
    eventName: Literal['wallet.transaction.analytics']
    transactionId: UUID


SourceStatus = RunSource | GiftSource | CaseSource | PurchaseSource | WalletSource
