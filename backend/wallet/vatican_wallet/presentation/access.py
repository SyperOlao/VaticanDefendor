import hmac
from fastapi import Request, Security
from fastapi.security import APIKeyHeader, HTTPBearer

service_scheme=APIKeyHeader(name="X-Service-Secret", scheme_name="ServiceSecret", auto_error=False)
bearer_scheme=HTTPBearer(scheme_name="BearerAuth", auto_error=False, description="accessToken или гостевой token auth, без префикса Bearer.")
from ..application.errors import ApplicationError


def service_access(request: Request, x_service_secret: str | None = Security(service_scheme)):
    secret = request.app.state.settings.service_secret
    if len(secret) < 32 or not x_service_secret or not hmac.compare_digest(secret, x_service_secret):
        raise ApplicationError(401, "Service credential required")


def player(request: Request, credential=Security(bearer_scheme)):
    scheme, _, token = request.headers.get("authorization", "").partition(" ")
    if scheme.lower() != "bearer" or not token or len(token) > 512 or any(c.isspace() for c in token):
        raise ApplicationError(401, "Bearer credential required")
    return request.app.state.auth.authenticate(token).account_id
