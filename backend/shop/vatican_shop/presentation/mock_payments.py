from uuid import UUID
from pydantic import BaseModel, ConfigDict, Field
from fastapi import APIRouter, Depends, Header, Query, Request
from fastapi.responses import JSONResponse
from dishka.integrations.fastapi import FromDishka, inject_sync
from starlette.concurrency import run_in_threadpool
from .access import bearer_token
from ..application.mock_payments import MockPayments
from ..application.errors import ApplicationError
from ..domain.mock_payments import Scenario

router=APIRouter(tags=['mock-test-payments'])


class Product(BaseModel):
    model_config=ConfigDict(extra='forbid')
    sku: str = Field(pattern=r'^[A-Za-z0-9_.-]{1,128}$', description='Внутренний TEST SKU. Сочетание sku/version неизменяемо.')
    version: int = Field(strict=True,ge=1,le=2_147_483_647, description='Новая цена или награда требует новой версии и нового externalSku.')
    externalSku: str = Field(pattern=r'^[A-Za-z0-9_.-]{1,255}$', description='Уникальный versioned SKU TEST-провайдера, например test.sword.v1.')
    rewardBundleId: UUID = Field(description='UUID уже опубликованного reward bundle game_backend. Только WEAPON, всего до 20 единиц.')
    priceMinor: int = Field(strict=True,ge=0,le=9_223_372_036_854_775_807, description='Условная цена в minor units: 199 USD = 1.99 USD. MOCK не списывает деньги.')
    currencyIso: str = Field(pattern=r'^[A-Z]{3}$', description='Три заглавные буквы валюты условной цены, например USD.')


class Availability(BaseModel):
    model_config=ConfigDict(extra='forbid')
    enabled: bool = Field(strict=True, description='Разрешить новые заказы. Принятые заказы не отменяются.')
    requestKey: UUID = Field(description='Ненулевой UUID команды. Для повтора сохраните ключ и исходное тело.')


class Order(BaseModel):
    model_config=ConfigDict(extra='forbid')
    productId: UUID = Field(description='UUID продукта из GET /v1/mock/products; цена и награда берутся с сервера.')
    requestKey: UUID = Field(description='Ненулевой UUID, сохраняемый до отправки. Повтор ключа возвращает тот же заказ; новый ключ — новая покупка.')


class Confirm(BaseModel):
    model_config=ConfigDict(extra='forbid')
    receipt: str = Field(min_length=1,max_length=8192, description='Непрозрачная подписанная строка из реального ответа simulate. Не JSON webhook и не самостоятельно созданный токен.')


class Simulate(BaseModel):
    model_config=ConfigDict(extra='forbid')
    requestKey: UUID = Field(description='UUID команды симулятора. Новый сценарий требует нового ключа; прежний ключ возвращает прежний ответ.')
    scenario: Scenario = Field(description='SUCCESS — оплата; DECLINED — отказ; PENDING — ожидание; TIMEOUT — потерян SDK-ответ; REFUND — возврат; DUPLICATE_WEBHOOK — прежнее событие.')
    providerUnavailable: bool = Field(default=False,strict=True, description='Симулировать отказ независимого lookup. Подписанный webhook при этом не разрешает выдачу.')


class Retry(BaseModel):
    model_config=ConfigDict(extra='forbid')
    requestKey: UUID = Field(description='UUID административного retry; сохраняйте для повтора после таймаута.')
    expectedVersion: int = Field(strict=True,ge=1,le=9_223_372_036_854_775_807, description='version текущего заказа из административного GET. Это не версия продукта.')
    reason: str = Field(min_length=1,max_length=500, description='Непустая причина после сверки провайдера и выдачи; сохраняется в журнале команды.')


@router.post('/v1/admin/shop/mock/products')
@inject_sync
def publish(body: Product,service: FromDishka[MockPayments],token: str=Depends(bearer_token),
            x_mock_control_key: str | None=Header(None)):
    result=service.publish(token,x_mock_control_key,body.model_dump())
    return JSONResponse(result,status_code=200 if result['replayed'] else 201)


@router.patch('/v1/admin/shop/mock/products/{identity}/availability')
@inject_sync
def availability(identity: UUID,body: Availability,service: FromDishka[MockPayments],token: str=Depends(bearer_token),
                 x_mock_control_key: str | None=Header(None)):
    return service.availability(token,x_mock_control_key,identity,body.enabled,body.requestKey)


@router.get('/v1/mock/products')
@inject_sync
def products(service: FromDishka[MockPayments],token: str=Depends(bearer_token),
             limit: int=Query(50,ge=1,le=100),after: UUID | None=None):
    return service.products(token,limit,after)


@router.post('/v1/mock/orders')
@inject_sync
def create(body: Order,service: FromDishka[MockPayments],token: str=Depends(bearer_token)):
    return JSONResponse(service.create(token,body.productId,body.requestKey),status_code=202)


@router.get('/v1/mock/orders/{identity}')
@inject_sync
def get(identity: UUID,service: FromDishka[MockPayments],token: str=Depends(bearer_token)):
    return service.get(token,identity)


@router.post('/v1/mock/orders/{identity}/confirm')
@inject_sync
def confirm(identity: UUID,body: Confirm,service: FromDishka[MockPayments],token: str=Depends(bearer_token)):
    return JSONResponse(service.confirm(token,identity,body.receipt),status_code=202)


@router.post('/v1/admin/shop/mock/orders/{identity}/simulate')
@inject_sync
def simulate(identity: UUID,body: Simulate,service: FromDishka[MockPayments],token: str=Depends(bearer_token),
             x_mock_control_key: str | None=Header(None)):
    return service.simulate(token,x_mock_control_key,identity,body.requestKey,body.scenario,body.providerUnavailable)


@router.get('/v1/admin/shop/mock/orders/{identity}')
@inject_sync
def inspect(identity: UUID,service: FromDishka[MockPayments],token: str=Depends(bearer_token),
            x_mock_control_key: str | None=Header(None)):
    return service.inspect(token,x_mock_control_key,identity)


@router.post('/v1/admin/shop/mock/orders/{identity}/retry')
@inject_sync
def retry(identity: UUID,body: Retry,service: FromDishka[MockPayments],token: str=Depends(bearer_token),
          x_mock_control_key: str | None=Header(None)):
    return service.retry(token,x_mock_control_key,identity,body.expectedVersion,body.requestKey,body.reason)


@router.post('/v1/mock/provider/webhook')
@inject_sync
async def webhook(request: Request,service: FromDishka[MockPayments],x_mock_signature: str | None=Header(None)):
    service.require()
    body=bytearray()
    async for chunk in request.stream():
        if len(body)+len(chunk)>16384:raise ApplicationError(413,'MOCK webhook is too large')
        body.extend(chunk)
    return await run_in_threadpool(service.webhook,bytes(body),x_mock_signature)
