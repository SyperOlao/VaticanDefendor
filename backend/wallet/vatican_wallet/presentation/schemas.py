from uuid import UUID
from pydantic import BaseModel, ConfigDict, Field
from ..application.catalog import EventName


class Message(BaseModel):
    model_config = ConfigDict(extra="forbid")
    messageId: UUID = Field(description="Постоянный ID доставки. Повтор использует то же тело и тот же ID.")
    eventName: str = Field(min_length=1, max_length=128, description="Тип финансовой команды.", json_schema_extra={"enum": list(EventName)})
    schemaVersion: int = Field(strict=True, ge=1, description="Сейчас поддерживается версия 1.", json_schema_extra={"const": 1})
    payload: dict = Field(description="Поля зависят от eventName. Выберите готовую команду в Request body → Examples.")


class Charge(BaseModel):
    model_config = ConfigDict(extra="forbid")
    accountId: UUID = Field(description="ID владельца кошелька из auth.")
    operationId: UUID = Field(description="ID покупки. Для возврата используйте этот же ID и сумму; для новой покупки нужен новый ID.")
    amount: int = Field(strict=True, ge=1, le=9_223_372_036_854_775_807, description="Целая сумма PRESTIGE больше нуля; строки, дроби и true отклоняются.")
