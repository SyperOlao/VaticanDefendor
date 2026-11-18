import hmac
from fastapi import Header, Request
from ..application.errors import ApplicationError


def service_access(request: Request, x_service_secret: str | None = Header(default=None)):
    secret = request.app.state.settings.service_secret
    if len(secret) < 32 or not x_service_secret or not hmac.compare_digest(secret, x_service_secret):
        raise ApplicationError(401, "Service credential required")


def bearer_token(request: Request):
    scheme, _, token = request.headers.get("authorization", "").partition(" ")
    if scheme.lower() != "bearer" or not token or len(token) > 512 or any(c.isspace() for c in token):
        raise ApplicationError(401, "Bearer credential required")
    return token


def player(request: Request):
    return request.app.state.auth.authenticate(bearer_token(request)).account_id
