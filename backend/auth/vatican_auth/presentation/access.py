import inspect

from dishka.integrations.fastapi import DishkaRoute, FromDishka, inject
from fastapi import Depends, Request

from ..application.access import AuthorizeUseCase
from ..application.errors import InvalidCredentials


def bearer_token(request: Request) -> str:
    scheme, _, token = request.headers.get("authorization", "").partition(" ")
    if scheme.lower() != "bearer" or not token:
        raise InvalidCredentials("Bearer credential required")
    return token


def require_permission(permission: str):
    def decorate(endpoint):
        endpoint.__auth_permissions__ = (*getattr(endpoint, "__auth_permissions__", ()), permission)
        return endpoint

    return decorate


def require_roles(*roles: str):
    if not roles:
        raise ValueError("At least one role is required")

    def decorate(endpoint):
        endpoint.__auth_roles__ = tuple(roles)
        return endpoint

    return decorate


def guard_for(permissions: tuple[str, ...], roles: tuple[str, ...]):
    @inject
    async def guard(request: Request, authorize: FromDishka[AuthorizeUseCase]):
        token = bearer_token(request)
        if permissions:
            for permission in permissions:
                await authorize.execute(token, permission, roles)
        elif roles:
            await authorize.execute(token, roles=roles)

    guard.__auth_guard__ = (permissions, roles)
    return guard


class AuthorizedRoute(DishkaRoute):
    """Turn declarative guards into FastAPI dependencies before endpoint injection."""

    def __init__(self, path, endpoint, **kwargs):
        original = inspect.unwrap(endpoint)
        permissions = getattr(original, "__auth_permissions__", ())
        roles = getattr(original, "__auth_roles__", ())
        dependencies = list(kwargs.pop("dependencies", None) or [])
        spec = (permissions, roles)
        if (permissions or roles) and not any(
                getattr(dependency.dependency, "__auth_guard__", None) == spec for dependency in dependencies
        ):
            dependencies.append(Depends(guard_for(permissions, roles)))
        super().__init__(path, endpoint, dependencies=dependencies, **kwargs)
