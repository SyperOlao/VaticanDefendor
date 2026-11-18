from ..migrate import SCHEMA_REVISION
from typing import Annotated, Literal
from uuid import UUID
from pydantic import BaseModel, ConfigDict, Field, WithJsonSchema
from ..application.catalog import MAX_SCORE
from ..migrate import SCHEMA_REVISION


class RunRatingPayload(BaseModel):
    """Documentation contract; incoming payload remains an unmodified dictionary."""
    model_config = ConfigDict(extra="forbid")
    accountId: str = Field(description="UUID владельца забега из auth.", json_schema_extra={"format": "uuid"})
    runId: str = Field(description="UUID забега. Один забег начисляет очки только один раз, даже при новом messageId.",
                       json_schema_extra={"format": "uuid"})
    boardCode: str = Field(description="Код заранее созданной доски. Для XP по умолчанию — run-xp.",
                          pattern=r"^[A-Za-z0-9_.-]{1,64}$", examples=["run-xp"])
    rulesetId: str = Field(description="Версия правил, закреплённая при старте забега.",
                          pattern=r"^[A-Za-z0-9_.-]{1,128}$", examples=["xp-v1"])
    contentReleaseId: str = Field(description="Релиз контента исходного забега.",
                                 pattern=r"^[A-Za-z0-9_.-]{1,128}$", examples=["sha256-" + "a" * 64])
    points: int = Field(ge=0, le=MAX_SCORE, description="XP этого забега, рассчитанные game_backend. "
                        "Только целое число; 0 допустим. Дробные значения и boolean отклоняются.",
                        json_schema_extra={"format": "int64"}, examples=[80, 0])


class Message(BaseModel):
    model_config = ConfigDict(extra="forbid")
    messageId: UUID = Field(description="Стабильный UUID доставки. Для повтора сохраните этот UUID и всё содержимое.")
    eventName: str = Field(min_length=1, max_length=128, description="Поддерживаемая команда — run.rating.",
                           json_schema_extra={"const": "run.rating"}, examples=["run.rating"])
    schemaVersion: int = Field(strict=True, ge=1, description="Поддерживаемая версия контракта — целое число 1.",
                               json_schema_extra={"const": 1}, examples=[1])
    # Schema-only annotation: coercion would change the immutable payload hash and stored receipts.
    payload: Annotated[dict, WithJsonSchema(RunRatingPayload.model_json_schema())] = Field(
        description="Вклад одного забега. Все шесть полей обязательны; дополнительные поля запрещены.")


class BoardDefinition(BaseModel):
    code: str = Field(description="Код доски для URL и boardCode в команде начисления.", examples=["run-xp"])
    scoreSource: str = Field(description="Источник очков. RUN_XP принимает run.rating; исторические источники "
                            "также доступны для чтения.", examples=["RUN_XP"])


class Boards(BaseModel):
    boards: list[BoardDefinition] = Field(description="Доски по возрастанию code, включая доски без участников.")


class RankedEntry(BaseModel):
    accountId: UUID = Field(description="UUID игрока в auth.")
    score: int = Field(ge=0, le=MAX_SCORE, description="Суммарные очки игрока на этой доске.", json_schema_extra={"format": "int64"})
    position: int = Field(ge=1, description="Абсолютное место с 1. При равных очках меньший UUID идёт раньше.")


class Ranking(BaseModel):
    board: str = Field(description="Код запрошенной доски.", examples=["run-xp"])
    entries: list[RankedEntry] = Field(description="Страница по убыванию score. Пустой список возможен и на существующей доске.")


class Standing(BaseModel):
    board: str = Field(description="Код запрошенной доски.", examples=["run-xp"])
    entry: RankedEntry | None = Field(description="Текущий игрок или null, если он ещё не участвовал. "
                                      "Участник с нулевыми очками имеет полноценную запись.")


class RunRatingReceipt(RunRatingPayload):
    boardId: str = Field(description="Внутренний UUID доски.", json_schema_extra={"format": "uuid"})
    scoreAfter: int = Field(ge=0, le=MAX_SCORE, description="Сумма после первоначального начисления. "
                           "При повторе возвращается историческое значение, а не текущий счёт.",
                           json_schema_extra={"format": "int64"})


class AppliedMessage(BaseModel):
    messageId: str = Field(description="UUID обработанного сообщения.", json_schema_extra={"format": "uuid"})
    status: Literal["APPLIED"] = Field(description="Начисление и квитанция сохранены одной транзакцией.")
    receipt: RunRatingReceipt = Field(description="Неизменяемая квитанция первоначального начисления за забег.")


class FailedCommand(BaseModel):
    messageId: str = Field(description="UUID команды.", json_schema_extra={"format": "uuid"})
    status: Literal["REJECTED", "REVIEW"] = Field(description="Отказ по содержимому либо необходимость проверки оператором.")
    code: str = Field(description="Машинный код причины, например CommandRejected или HandlerUnavailable.")


class BrokerEventStatus(BaseModel):
    eventId: str = Field(description="Стабильный UUID события результата.", json_schema_extra={"format": "uuid"})
    destination: Literal["rabbit.results", "rabbit.dead"] = Field(description="Результат для game_backend или очередь отказов.")
    attempts: int = Field(ge=0, description="Количество начатых попыток публикации события.")
    status: Literal["PENDING", "PUBLISHED", "REVIEW"] = Field(
        description="Ожидание публикации, publisher confirm от RabbitMQ или остановка для проверки. "
        "PUBLISHED ещё не означает обработку результата в game_backend.")


class BrokerCommandStatus(BaseModel):
    messageId: str = Field(description="UUID исходной AMQP-команды.", json_schema_extra={"format": "uuid"})
    status: Literal["PENDING", "APPLIED", "REJECTED", "REVIEW"] = Field(description="Состояние обработки команды в rating.")
    attempts: int = Field(ge=0, description="Начатые попытки обработки в текущем поколении доставки.")
    deliveryGeneration: int = Field(ge=1, description="Поколение доставки. Увеличивается после ручного replay команды.")
    lastError: str | None = Field(description="Последний безопасный код ошибки либо null. Может сохраняться до успешной обработки.")
    result: AppliedMessage | FailedCommand | None = Field(description="Сохранённый результат; null, пока обработка не завершена.")
    events: list[BrokerEventStatus] = Field(description="События результата и отказа, включая предыдущие поколения replay.")


class ValidationIssue(BaseModel):
    field: str = Field(description="Путь поля, например query.limit. Для бизнес-ошибок список errors обычно пуст.")
    type: str = Field(description="Машинный тип ошибки проверки.")
    message: str = Field(description="Безопасное описание на английском, без исходного значения.")


class ErrorResponse(BaseModel):
    code: str = Field(description="Машинный код ошибки.", examples=["ResourceNotFound"])
    detail: str = Field(description="Безопасное пояснение на английском. Внутренние исключения и SQL не возвращаются.")
    requestId: UUID = Field(description="UUID для поиска запроса в логах; совпадает с заголовком X-Request-ID.")
    errors: list[ValidationIssue] = Field(description="Ошибки полей. Пустой список, если ошибка не связана с валидацией формы запроса.")


class Liveness(BaseModel):
    status: Literal["live"] = Field(description="HTTP-процесс отвечает; зависимости не проверялись.")


class Readiness(BaseModel):
    status: Literal["ready"] = Field(description="PostgreSQL доступна и ревизия схемы соответствует приложению.")
    schemaRevision: str = Field(description="Текущая ревизия миграций rating.", examples=[SCHEMA_REVISION])
