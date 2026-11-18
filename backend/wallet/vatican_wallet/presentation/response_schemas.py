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


class CurrencyBalance(Model):
    currencyId: str = Field(description='Код валюты.')
    balance: int = Field(ge=0, description='Текущее количество валюты.')
    revision: int = Field(ge=1, description='Версия баланса валюты.')


class WalletBalance(CurrencyBalance):
    accountId: UUID = Field(description='Аккаунт из пользовательского токена.')
    balances: list[CurrencyBalance] = Field(description='Все открытые балансы аккаунта. Верхние balance/currencyId относятся к PRESTIGE.')


class Transaction(Model):
    transactionId: UUID = Field(description='UUID транзакции; используется как before для следующей страницы.')
    currencyId: str = Field(description='Код валюты.')
    delta: int = Field(description='Изменение: положительное — начисление, отрицательное — списание.')
    balanceAfter: int = Field(ge=0, description='Баланс сразу после этой транзакции.')
    reason: str = Field(description='Причина изменения баланса.')
    sourceType: str = Field(description='Тип бизнес-источника: покупка, награда, возврат и т.д.')
    sourceId: UUID = Field(description='UUID бизнес-источника.')
    itemIndex: int = Field(ge=0, description='Индекс строки награды или операции.')
    createdAt: datetime = Field(description='Время записи в UTC.')


class History(Model):
    transactions: list[Transaction] = Field(description='Транзакции от новых к старым; при равном времени — по UUID.')
    nextCursor: UUID | None = Field(description='Передайте как before. null означает последнюю страницу.')


class ChangeBase(Model):
    accountId: UUID = Field(description='Аккаунт исходной команды.')
    operationId: UUID = Field(description='Стабильный UUID покупки. Возврат использует тот же UUID.')
    currencyId: Literal['PRESTIGE'] = Field(description='Валюта магазинного списания или возврата.')
    amount: int = Field(ge=1, description='Положительная сумма исходной команды.')


class AppliedChange(ChangeBase):
    status: Literal['APPLIED'] = Field(description='Изменение сохранено; повтор вернёт тот же результат.')
    balance: int = Field(ge=0, description='Баланс после выполнения команды.')


class RejectedChange(ChangeBase):
    status: Literal['REJECTED'] = Field(description='Бизнес-отказ сохранён; HTTP 200 не означает успешное списание.')
    code: Literal['InsufficientFunds', 'ChargeNotFound'] = Field(description='Недостаточно денег либо не найдено исходное списание для возврата.')


class AppliedMessage(Model):
    messageId: UUID = Field(description='UUID обработанного сообщения.')
    status: Literal['APPLIED'] = Field(description='Команда обработана. Проверяйте также бизнес-результат внутри receipt.')
    receipt: dict[str, Any] = Field(description='Неизменяемый чек конкретного типа команды: награда, оплата кейса, улучшение или покупка. Может содержать бизнес-отказ.')


class BrokerEvent(Model):
    eventId: UUID
    destination: str
    attempts: int
    status: Literal['PENDING', 'PUBLISHED', 'REVIEW']


class CommandStatus(Model):
    messageId: UUID
    payloadSha256: str = Field(description='SHA-256 канонической команды; отправитель обязан сверять его.')
    status: Literal['PENDING', 'APPLIED', 'REJECTED', 'REVIEW']
    attempts: int
    deliveryGeneration: int = Field(ge=1, description='Поколение обработки; увеличивается при явном replay.')
    lastError: str | None
    result: dict[str, Any] | None = Field(description='Сохранённый ответ команды; null до обработки.')
    events: list[BrokerEvent] = Field(description='Связанные публикации dead-letter. Результаты читаются через gRPC, без отдельной очереди результатов.')


class Changed(Model):
    changed: bool = Field(description='true — состояние изменено; false — операция отсутствует или её состояние не допускает replay.')
