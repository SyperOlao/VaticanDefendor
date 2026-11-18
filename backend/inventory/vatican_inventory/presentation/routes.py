from uuid import UUID
from dishka.integrations.fastapi import FromDishka, inject_sync
from fastapi import APIRouter, Body, Depends, Path, Query
from typing import Annotated, Literal
from .access import player, service_access
from .schemas import (Message, Loadout, Grant, Weapons, Inventory, ItemDetail, WeaponDetail,
                      Consumption, ConsumptionResult, LoadoutResult, GrantResult, OwnedWeaponsPage, Liveness, Readiness)
from .documentation import operation
from . import examples as ex
from ..application.use_cases import InventoryUseCases
from ..application.health import HealthUseCases
from ..application.broker import BrokerOperations
from ..application.browse import BrowseInventory
from ..domain.broker import InspectionKind

router = APIRouter()
AccountPath = Annotated[UUID, Path(description="UUID игрока из auth; доступен только доверенному сервису.", examples=[ex.ACCOUNT])]
OperationPath = Annotated[UUID, Path(description="UUID исходной операции выдачи или списания.", examples=[ex.OPERATION])]
MessagePath = Annotated[UUID, Path(description="messageId команды, опубликованной в RabbitMQ.", examples=[ex.MESSAGE])]
EventPath = Annotated[UUID, Path(description="eventId публикации из списка событий или записи карантина.", examples=[ex.EVENT])]
RejectionPath = Annotated[UUID, Path(description="rejectionId из списка карантина; это не eventId.", examples=[ex.REJECTION])]


def page_size(default=50):
    return Query(default, ge=1, le=100, description="Размер страницы: 1..100. По умолчанию 50 при включённой пагинации.", examples=[50])


def page_cursor():
    return Query(None, max_length=2048, description="Значение nextCursor из предыдущего ответа. В первом запросе не передавайте. Сохраняйте прежние фильтры.")


@router.get("/health/live", response_model=Liveness, **operation("live"))
def live():
    return {"status": "live"}


@router.get("/health/ready", response_model=Readiness, **operation("ready"))
@inject_sync
def ready(use_case: FromDishka[HealthUseCases]):
    return use_case.ready()


@router.post("/internal/messages", dependencies=[Depends(service_access)], **operation("receive"))
@inject_sync
def receive(body: Annotated[Message, Body(openapi_examples=ex.COMMAND_EXAMPLES)], use_case: FromDishka[InventoryUseCases]):
    return use_case.receive(body.messageId, body.eventName, body.schemaVersion, body.payload)


@router.get("/internal/broker/commands/{message_id}", dependencies=[Depends(service_access)], **operation("broker_status"))
@inject_sync
def broker_status(message_id: MessagePath, use_case: FromDishka[BrokerOperations]):
    return use_case.get(message_id)


@router.get("/internal/broker/commands", dependencies=[Depends(service_access)], **operation("broker_commands"))
@inject_sync
def broker_commands(use_case: FromDishka[BrokerOperations], limit: int = page_size(),
                    cursor: str | None = page_cursor(),
                    status: str | None = Query(None, description="Фильтр исполнения: PENDING, APPLIED, REJECTED или REVIEW. Без фильтра — все команды.", examples=["REVIEW"])):
    return use_case.list(InspectionKind.COMMANDS, limit, cursor, status)


@router.get("/internal/broker/events", dependencies=[Depends(service_access)], **operation("broker_events"))
@inject_sync
def broker_events(use_case: FromDishka[BrokerOperations], limit: int = page_size(),
                  cursor: str | None = page_cursor(),
                  status: str | None = Query(None, description="Фильтр публикации: PENDING, PUBLISHED или REVIEW. Не совпадает со статусами исполнения команд.", examples=["REVIEW"])):
    return use_case.list(InspectionKind.EVENTS, limit, cursor, status)


@router.get("/internal/broker/events/{event_id}", dependencies=[Depends(service_access)], **operation("broker_event"))
@inject_sync
def broker_event(event_id: EventPath, use_case: FromDishka[BrokerOperations]):
    return use_case.detail(InspectionKind.EVENTS, event_id)


@router.get("/internal/broker/rejections", dependencies=[Depends(service_access)], **operation("broker_rejections"))
@inject_sync
def broker_rejections(use_case: FromDishka[BrokerOperations], limit: int = page_size(),
                      cursor: str | None = page_cursor(),
                      code: str | None = Query(None, description="Безопасный код BrokerCode, например InvalidEnvelope или MessageConflict. Без фильтра — весь карантин.", examples=["InvalidEnvelope"])):
    return use_case.list(InspectionKind.REJECTIONS, limit, cursor, code)


@router.get("/internal/broker/rejections/{rejection_id}", dependencies=[Depends(service_access)], **operation("broker_rejection"))
@inject_sync
def broker_rejection(rejection_id: RejectionPath, use_case: FromDishka[BrokerOperations]):
    return use_case.detail(InspectionKind.REJECTIONS, rejection_id)


@router.get("/v1/weapons", response_model=Weapons, response_model_exclude_none=True, **operation("inventory"))
@inject_sync
def inventory(use_case: FromDishka[InventoryUseCases], account_id: UUID = Depends(player)):
    return use_case.get(account_id)


@router.put("/v1/weapons/loadout", response_model=Weapons, response_model_exclude_none=True, **operation("loadout"))
@inject_sync
def loadout(body: Annotated[Loadout, Body(openapi_examples={
        "guarded": ex.example("С ревизией и ключом для повторов", ex.LOADOUT),
        "minimal": ex.example("Минимальный выбор без защиты ревизией", {"InstanceIds": [ex.WEAPON]})})],
        use_case: FromDishka[InventoryUseCases], account_id: UUID = Depends(player)):
    return use_case.set_loadout(account_id, body.InstanceIds, body.expectedRevision, body.requestKey)


@router.get("/v1/weapons/loadout/operations/{request_key}", response_model=LoadoutResult, **operation("loadout_result"))
@inject_sync
def loadout_result(request_key: Annotated[UUID, Path(description="requestKey из прежнего PUT /v1/weapons/loadout.", examples=[ex.OPERATION])],
                   use_case: FromDishka[InventoryUseCases], account_id: UUID = Depends(player)):
    return use_case.loadout_result(account_id, request_key)


@router.get("/v1/weapons/owned", response_model=OwnedWeaponsPage, **operation("owned_weapons"))
@inject_sync
def owned_weapons(use_case: FromDishka[BrowseInventory], account_id: UUID = Depends(player),
                  limit: int = page_size(), cursor: str | None = page_cursor(),
                  configurationId: str | None = Query(None, min_length=1, max_length=80,
                    description="Необязательный фильтр по ID определения оружия. На следующих страницах оставляйте прежнее значение.", examples=["sword.common"])):
    return use_case.weapons(account_id, limit, cursor, configurationId)


@router.get("/v1/weapons/{instance_id}", response_model=WeaponDetail, **operation("weapon"))
@inject_sync
def weapon(instance_id: Annotated[UUID, Path(description="InstanceId принадлежащего игроку экземпляра, не ConfigurationId.", examples=[ex.WEAPON])],
           use_case: FromDishka[InventoryUseCases], account_id: UUID = Depends(player)):
    return use_case.weapon(account_id, instance_id)


@router.get("/internal/players/{account_id}/snapshot", dependencies=[Depends(service_access)],
            response_model=Weapons, response_model_exclude_none=True, **operation("snapshot"))
@inject_sync
def snapshot(account_id: AccountPath, use_case: FromDishka[InventoryUseCases]):
    return use_case.get(account_id, selected_only=True)


@router.post("/internal/grants", dependencies=[Depends(service_access)], response_model=GrantResult, response_model_exclude_none=True,
             **operation("grant"))
@inject_sync
def grant(body: Annotated[Grant, Body(openapi_examples={"purchase": ex.example("Выдача по UUID покупки", ex.GRANT)})],
          use_case: FromDishka[InventoryUseCases]):
    return use_case.grant(body.accountId, body.operationId, body.configurationId)


@router.get("/internal/players/{account_id}/grants/{operation_id}", dependencies=[Depends(service_access)],
            response_model=GrantResult, response_model_exclude_none=True, **operation("grant_result"))
@inject_sync
def grant_result(account_id: AccountPath, operation_id: OperationPath, use_case: FromDishka[InventoryUseCases]):
    return use_case.grant_result(account_id, operation_id)


@router.post("/internal/items/consumptions", dependencies=[Depends(service_access)],
             response_model=ConsumptionResult, response_model_exclude_none=True, **operation("consume"))
@inject_sync
def consume(body: Annotated[Consumption, Body(openapi_examples={"consume": ex.example("Списать 2 ремонтных набора", ex.CONSUMPTION)})],
            use_case: FromDishka[InventoryUseCases]):
    return use_case.item_consumption.execute(body.accountId, body.operationId, body.itemId, body.quantity)


@router.get("/internal/players/{account_id}/item-consumptions/{operation_id}", dependencies=[Depends(service_access)],
            response_model=ConsumptionResult, response_model_exclude_none=True, **operation("consumption"))
@inject_sync
def consumption(account_id: AccountPath, operation_id: OperationPath, use_case: FromDishka[InventoryUseCases]):
    return use_case.item_consumption.get(account_id, operation_id)


@router.get("/v1/inventory", response_model=Inventory, response_model_exclude_unset=True, **operation("items"))
@inject_sync
def items(use_case: FromDishka[InventoryUseCases], browse: FromDishka[BrowseInventory], account_id: UUID = Depends(player),
          limit: int | None = page_size(None), cursor: str | None = page_cursor(),
          kind: Literal["ITEM", "CASE"] | None = Query(None, description="ITEM — ключи item:*, CASE — case:*. Без фильтра видны также исходные legacy-ключи.", examples=["ITEM"])):
    if limit is not None or cursor is not None or kind is not None:
        return browse.items(account_id, limit or 50, cursor, kind)
    return use_case.items(account_id)


@router.get("/v1/inventory/{item_id}", response_model=ItemDetail, **operation("item"))
@inject_sync
def item(item_id: Annotated[str, Path(description="Исходный ключ предмета или кейса, включая префикс item: или case:.", examples=["item:repair-kit"])],
         use_case: FromDishka[InventoryUseCases], account_id: UUID = Depends(player)):
    return use_case.item(account_id, item_id)
