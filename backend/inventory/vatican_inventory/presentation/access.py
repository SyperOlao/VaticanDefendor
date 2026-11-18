import hmac
from fastapi import Depends, Request, Security
from fastapi.security import APIKeyHeader, HTTPBearer
from dishka.integrations.fastapi import FromDishka, inject_sync
from ..application.access import AuthenticatePlayer
from ..application.errors import ApplicationError


service_key = APIKeyHeader(name="X-Service-Secret", scheme_name="ServiceSecret", auto_error=False,
    description="Сервисный секрет VD_SERVICE_SECRET для внутренних методов. Вставьте только значение; Bearer не нужен.")


def service_access(request: Request, x_service_secret: str | None = Security(service_key)):
    secret = request.app.state.settings.service_secret
    if len(secret) < 32 or not x_service_secret or not hmac.compare_digest(secret.encode(), x_service_secret.encode()):
        raise ApplicationError(401, "Service credential required")


bearer = HTTPBearer(auto_error=False, description="Токен auth: token гостя или accessToken сессии. Вставьте только токен, без Bearer.")


@inject_sync
def player(request: Request, use_case: FromDishka[AuthenticatePlayer], _=Depends(bearer)):
    return use_case.execute(request.headers.get("authorization")).account_id
