from typing import Annotated
from uuid import UUID
from ..migrate import SCHEMA_REVISION
from dishka.integrations.fastapi import FromDishka, inject_sync
from fastapi import APIRouter, Body, Depends, Path, Query
from .access import player, service_access
from .schemas import (AppliedMessage, Boards, BrokerCommandStatus, Liveness, Message,
                      Ranking, Readiness, Standing)
from .openapi import (APPLIED, BROKER_EXAMPLES, COMMAND_EXAMPLES, ENTRY, HEALTH_TAG,
                      INTERNAL_TAG, RATING_TAG, ZERO_APPLIED, ZERO_ENTRY,
                      error_responses, example, success)
from ..application.use_cases import RatingUseCases
from ..application.health import HealthUseCases
from ..application.broker import BrokerOperations
from ..migrate import SCHEMA_REVISION

router = APIRouter()

BoardCode = Annotated[str, Path(description="Код доски из GET /v1/leaderboards. "
    "От 1 до 64 символов: латинские буквы, цифры, _, точка и дефис. Регистр учитывается.", examples=["run-xp"],
    json_schema_extra={"minLength": 1, "maxLength": 64, "pattern": r"^[A-Za-z0-9_.-]{1,64}$"})]
PageLimit = Annotated[int, Query(ge=1, le=100, description="Максимальное число записей на странице.", examples=[50])]
PageOffset = Annotated[int, Query(ge=0, le=100000, description="Сколько записей пропустить. "
    "Например, limit=10, offset=10 — вторая страница.", examples=[0])]


@router.get("/health/live", tags=[HEALTH_TAG], summary="Проверить, что HTTP-процесс работает",
    description="Не обращается к PostgreSQL, auth или RabbitMQ. Ответ live не гарантирует готовность начислений.",
    responses={200: success(Liveness, "HTTP-процесс отвечает.", {"live": example("Процесс работает", {"status": "live"})}),
               **error_responses(500)})
def live():
    return {"status": "live"}


@router.get("/health/ready", tags=[HEALTH_TAG], summary="Проверить базу данных и миграции",
    description="Проверяет соединение с PostgreSQL и ровно одну ожидаемую ревизию rating. "
        "Не проверяет auth, RabbitMQ и worker-процессы; их готовность оценивается отдельно.",
    responses={200: success(Readiness, "База и схема готовы.", {"ready": example("Сервис готов", {
        "status": "ready", "schemaRevision": SCHEMA_REVISION})}), **error_responses(500, 503, readiness=True)})
@inject_sync
def ready(use_case: FromDishka[HealthUseCases]):
    return use_case.ready()


@router.get("/v1/leaderboards", response_model=Boards, tags=[RATING_TAG], summary="Посмотреть доступные доски",
    description="Публичный список по возрастанию code, включая доски без участников и исторические источники очков. "
        "Авторизация не требуется. Нет поля total; пустой boards означает отсутствие записей на выбранной странице.",
    responses={200: success(Boards, "Страница досок.", {
        "boards": example("Обычная доска XP", {"boards": [{"code": "run-xp", "scoreSource": "RUN_XP"}]}),
        "empty": example("Пустая страница", {"boards": []})}), **error_responses(422, 500, 503)})
@inject_sync
def boards(use_case: FromDishka[RatingUseCases], limit: PageLimit = 50, offset: PageOffset = 0):
    return use_case.boards(limit, offset)


@router.get("/v1/leaderboards/{code}", response_model=Ranking, tags=[RATING_TAG], summary="Получить таблицу лидеров",
    description="Публичная страница игроков по убыванию суммы очков. При равенстве раньше идёт меньший UUID; "
        "места уникальны и начинаются с 1. position — место во всей доске, а не номер строки страницы. "
        "Нет поля total. entries=[] означает пустую доску или страницу за её концом; неизвестная доска даёт 404. "
        "При новых начислениях позиции между запросами могут меняться.",
    responses={200: success(Ranking, "Текущая страница рейтинга.", {
        "players": example("80 XP и участник с нулевыми очками", {"board": "run-xp", "entries": [ENTRY, ZERO_ENTRY]}),
        "empty": example("Доска существует, участников ещё нет", {"board": "run-xp", "entries": []})}),
        **error_responses(404, 422, 500, 503, validation="ranking")})
@inject_sync
def ranking(code: BoardCode, use_case: FromDishka[RatingUseCases], limit: PageLimit = 50, offset: PageOffset = 0):
    return use_case.ranking(code, limit, offset)


@router.get("/v1/leaderboards/{code}/me", response_model=Standing, tags=[RATING_TAG], summary="Узнать своё место",
    description="Нажмите Authorize → HTTPBearer и вставьте токен игрока из auth. Владелец определяется через auth; "
        "чужой accountId передать нельзя. Возвращает собственное абсолютное место, в том числе за пределами offset=100000. "
        "entry=null — игрок ещё не участвовал; чтение не создаёт запись с нулевыми очками. Неизвестная доска даёт 404. "
        "Если auth недоступен, возвращается 503.",
    responses={200: success(Standing, "Позиция текущего игрока либо отсутствие участия.", {
        "ranked": example("Игрок уже участвовал", {"board": "run-xp", "entry": ENTRY}),
        "unranked": example("Игрок ещё не участвовал", {"board": "run-xp", "entry": None})}),
        **error_responses(401, 404, 422, 500, 503, auth=True, validation="board")})
@inject_sync
def standing(code: BoardCode, use_case: FromDishka[RatingUseCases], account_id: UUID = Depends(player)):
    return use_case.standing(code, account_id)


@router.post("/internal/messages", dependencies=[Depends(service_access)], tags=[INTERNAL_TAG],
    summary="Начислить XP за забег через HTTP",
    description="Синхронный серверный метод, альтернатива RabbitMQ. Authorize → ServiceSecret добавляет "
        "X-Service-Secret. Принимает только run.rating версии 1, доска должна существовать и иметь источник RUN_XP. "
        "Все шесть полей payload обязательны. Очки рассчитывает game_backend.\n\n"
        "Первый запрос и точный повтор возвращают 200 APPLIED. Сохранение очков, квитанции и дедупликации атомарно. "
        "При потере ответа/503 повторите тот же messageId и неизменный payload, сохраняя значения и написание UUID. "
        "Новый messageId с тем же runId и тем же payload тоже не удваивает очки. Изменение уже обработанного payload "
        "или переполнение суммы дают 409. receipt.scoreAfter остаётся историческим значением первого начисления.\n\n"
        "Максимум тела — 16 KiB; на получение тела отводится 5 секунд. HTTP-доставка не создаёт AMQP inbox: "
        "GET /internal/messages/{message_id} для такого запроса вернёт 404.",
    responses={200: success(AppliedMessage, "Начисление сохранено либо возвращена прежняя квитанция.", {
        "applied": example("80 XP сохранены на новой записи", APPLIED),
        "zero": example("Нулевой забег тоже сохранён", ZERO_APPLIED)}),
        **error_responses(400, 401, 408, 409, 413, 422, 500, 503, credential="service", validation="message")})
@inject_sync
def receive(body: Annotated[Message, Body(openapi_examples=COMMAND_EXAMPLES)], use_case: FromDishka[RatingUseCases]):
    return use_case.receive(body.messageId, body.eventName, body.schemaVersion, body.payload)


@router.get("/internal/messages/{message_id}", dependencies=[Depends(service_access)], tags=[INTERNAL_TAG],
    summary="Проверить обработку RabbitMQ-команды",
    description="Только AMQP inbox, без изменения состояния. Требуется Authorize → ServiceSecret. "
        "Возьмите messageId исходной команды game_backend. HTTP-начисления здесь не появляются.\n\n"
        "**PENDING** — ожидает обработки/повтора; **APPLIED** — очки сохранены; **REJECTED** — содержимое отклонено; "
        "**REVIEW** — автоматические попытки исчерпаны, нужна проверка оператором. "
        "events показывает публикацию результатов: PENDING, PUBLISHED (есть publisher confirm) или REVIEW. "
        "PUBLISHED не гарантирует COMPLETED в game_backend до обработки результата game_rating_results. "
        "Ручной replay доступен через CLI, отдельного HTTP-метода нет.",
    responses={200: success(BrokerCommandStatus, "Состояние команды и доставки её результатов.", BROKER_EXAMPLES),
        **error_responses(401, 404, 422, 500, 503, credential="service", missing="broker", validation="broker")})
@inject_sync
def message_status(message_id: Annotated[UUID, Path(description="messageId исходной команды, доставленной через RabbitMQ.",
        examples=["33333333-3333-4333-8333-333333333333"])], use_case: FromDishka[BrokerOperations]):
    return use_case.get(message_id)
