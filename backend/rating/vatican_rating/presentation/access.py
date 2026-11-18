import hmac
from fastapi import Depends, Request
from fastapi.security import APIKeyHeader, HTTPBearer
from dishka.integrations.fastapi import FromDishka, inject_sync
from ..application.access import AuthenticatePlayer
from ..application.errors import ApplicationError


def check_service_secret(secret: str, supplied: str | None):
    if (not supplied or not supplied.isascii() or len(supplied) != len(secret)
            or not hmac.compare_digest(secret, supplied)):
        raise ApplicationError(401, "Service credential required")


service_key = APIKeyHeader(name="X-Service-Secret", scheme_name="ServiceSecret", auto_error=False,
    description="Сервисный секрет VD_SERVICE_SECRET. Вставьте только значение: Swagger добавит заголовок "
                "X-Service-Secret. Используется только доверенными серверами для внутренних методов.")


def service_access(request: Request, x_service_secret: str | None = Depends(service_key)):
    check_service_secret(request.app.state.settings.service_secret, x_service_secret)


bearer = HTTPBearer(auto_error=False, scheme_name="HTTPBearer", bearerFormat="Opaque",
    description="Токен текущего игрока из auth: гостевой token или accessToken после входа. "
                "Вставьте только токен, без префикса Bearer; Swagger добавит его сам. Не используйте refreshToken.")


@inject_sync
def player(request: Request, use_case: FromDishka[AuthenticatePlayer], _=Depends(bearer)):
    return use_case.execute(request.headers.get("authorization")).account_id
