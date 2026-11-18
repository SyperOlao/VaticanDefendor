from dishka.integrations.fastapi import FromDishka, inject_sync
from fastapi import APIRouter, Body, Depends, Security
from fastapi.security import APIKeyHeader
from typing import Annotated
from .access import service_access
from .schemas import MockGrant, MockGrantResult
from .documentation import operation
from . import examples as ex
from ..application.mock_payments import MockPaymentGrants

router = APIRouter()
mock_key = APIKeyHeader(name='X-Mock-Grant-Key', scheme_name='MockGrantSecret', auto_error=False,
    description='Отдельный VD_MOCK_GRANT_SECRET в test/development. Требуется вместе с ServiceSecret, не заменяет его.')


@router.post('/internal/mock-payment-grants', dependencies=[Depends(service_access)], response_model=MockGrantResult,
             **operation('mock_grant'))
@inject_sync
def grant(body: Annotated[MockGrant, Body(openapi_examples={'mock': ex.example('Один экземпляр из TEST-набора', ex.MOCK_GRANT)})],
          service: FromDishka[MockPaymentGrants], x_mock_grant_key: str | None = Security(mock_key)):
    payload=body.model_dump(mode='json')
    return service.grant(payload,x_mock_grant_key)
