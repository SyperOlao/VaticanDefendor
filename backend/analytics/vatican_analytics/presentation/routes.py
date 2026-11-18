import hmac
from uuid import UUID
from dishka.integrations.fastapi import FromDishka, inject_sync
from fastapi import APIRouter, Body, Depends, Request, Security
from fastapi.security import APIKeyHeader
from typing import Annotated
from .schemas import Message
from .documentation import operation, REQUEST_EXAMPLES
from fastapi.responses import JSONResponse
from pydantic import BaseModel, ConfigDict, Field
from ..application.errors import ApplicationError
from ..application.health import HealthUseCases
from ..application.use_cases import AnalyticsUseCases
from ..application.catalog import MessageStatus

router=APIRouter()
service_scheme=APIKeyHeader(name="X-Service-Secret", scheme_name="ServiceSecret", auto_error=False)


def service_access(request: Request,x_service_secret: str | None=Security(service_scheme)):
    secret=request.app.state.settings.service_secret
    if len(secret)<32 or not x_service_secret or not hmac.compare_digest(secret,x_service_secret):
        raise ApplicationError(401,'Service credential required')


@router.get('/health/live', **operation('live'))
def live():return {'status':'live'}


@router.get('/health/ready', **operation('ready'))
@inject_sync
def ready(use_case: FromDishka[HealthUseCases]):return use_case.ready()


@router.post('/internal/messages',dependencies=[Depends(service_access)], **operation('receive'))
@inject_sync
def receive(body: Annotated[Message, Body(openapi_examples=REQUEST_EXAMPLES)],use_case: FromDishka[AnalyticsUseCases]):
    result=use_case.receive(body.messageId,body.eventName,body.schemaVersion,body.payload)
    return JSONResponse(result,status_code=200 if result['status']==MessageStatus.APPLIED else 409 if result['status']==MessageStatus.REVIEW else 202)


@router.get('/internal/runs/{run_id}',dependencies=[Depends(service_access)], **operation('status'))
@inject_sync
def status(run_id: UUID,use_case: FromDishka[AnalyticsUseCases]):return use_case.get(run_id)


@router.get('/internal/sources/{kind}/{source_id}',dependencies=[Depends(service_access)], **operation('source_status'))
@inject_sync
def source_status(kind: str,source_id: UUID,use_case: FromDishka[AnalyticsUseCases]):
    from ..domain.events import EVENT, GIFT_EVENT, CASE_EVENT, PURCHASE_EVENT, WALLET_EVENT
    event={'run':EVENT,'level-gift':GIFT_EVENT,'case':CASE_EVENT,'shop-purchase':PURCHASE_EVENT,'wallet-transaction':WALLET_EVENT}.get(kind)
    if event is None:raise ApplicationError(404,'Analytics source kind not found')
    return use_case.get(source_id,event)
