from uuid import UUID
from dishka.integrations.fastapi import FromDishka, inject_sync
from fastapi import APIRouter, Body, Depends, Query
from typing import Annotated
from .documentation import operation, ex, CHANGE, REWARD
from .access import player, service_access
from .schemas import Charge, Message
from ..application.catalog import CurrencyCode
from ..application.use_cases import WalletUseCases
from ..application.health import HealthUseCases
from ..application.broker import BrokerOperations

router = APIRouter()


@router.get("/health/live", **operation('live'))
def live():
    return {"status": "live"}


@router.get("/health/ready", **operation('ready'))
@inject_sync
def ready(use_case: FromDishka[HealthUseCases]):
    return use_case.ready()


@router.post("/internal/messages", dependencies=[Depends(service_access)], **operation('receive'))
@inject_sync
def receive(body: Annotated[Message, Body(openapi_examples={"reward": ex("Начислить награду забега", REWARD)})], use_case: FromDishka[WalletUseCases]):
    return use_case.receive(body.messageId, body.eventName, body.schemaVersion, body.payload)


@router.get("/v1/wallet", **operation('balance'))
@inject_sync
def balance(use_case: FromDishka[WalletUseCases], account_id: UUID = Depends(player)):
    return use_case.balance(account_id)


@router.get("/v1/wallet/transactions", **operation('history'))
@inject_sync
def history(use_case: FromDishka[WalletUseCases], account_id: UUID = Depends(player),
            currencyId: CurrencyCode | None = None, before: UUID | None = None,
            limit: int = Query(default=50, ge=1, le=100, description="Количество транзакций, от 1 до 100.")):
    return use_case.history(account_id, currencyId, before, limit)


@router.post("/internal/charges", dependencies=[Depends(service_access)], **operation('charge'))
@inject_sync
def charge(body: Annotated[Charge, Body(openapi_examples={"purchase": ex("Исходная покупка", {k:v for k,v in CHANGE.items() if k != "currencyId"})})], use_case: FromDishka[WalletUseCases]):
    # A business refusal is a persisted command result, not a transport conflict.
    return use_case.change(body.accountId, body.operationId, body.amount)


@router.post("/internal/refunds", dependencies=[Depends(service_access)], **operation('refund'))
@inject_sync
def refund(body: Annotated[Charge, Body(openapi_examples={"purchase": ex("Исходная покупка", {k:v for k,v in CHANGE.items() if k != "currencyId"})})], use_case: FromDishka[WalletUseCases]):
    return use_case.change(body.accountId, body.operationId, body.amount, refund=True)


@router.get('/internal/broker/commands/{message_id}', dependencies=[Depends(service_access)], **operation('command_status'))
@inject_sync
def command_status(message_id: UUID, operations: FromDishka[BrokerOperations]):
    return operations.get(message_id)


@router.post('/internal/broker/commands/{message_id}/replay', dependencies=[Depends(service_access)], **operation('replay_command'))
@inject_sync
def replay_command(message_id: UUID, operations: FromDishka[BrokerOperations]):
    return {'changed': operations.replay(message_id)}


@router.post('/internal/broker/events/{event_id}/replay', dependencies=[Depends(service_access)], **operation('replay_event'))
@inject_sync
def replay_event(event_id: UUID, operations: FromDishka[BrokerOperations]):
    return {'changed': operations.replay(event_id, outbox=True)}
