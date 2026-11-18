from contextlib import asynccontextmanager
from fastapi import FastAPI
from dishka.integrations.fastapi import setup_dishka
from ..bootstrap.settings import Settings
from ..bootstrap.di import create_container
from dishka import Provider, Scope, provide
from ..application.ports import AuthGateway
from .routes import router
from .errors import install_error_handling
from .documentation import DESCRIPTION, TAGS, configure_openapi


def create_app(settings=None, *, providers=(), auth_gateway=None):
    settings = settings or Settings.from_environment()
    if auth_gateway is not None:
        class AuthProvider(Provider):
            @provide(scope=Scope.APP, provides=AuthGateway)
            def auth(self) -> AuthGateway:
                return auth_gateway
        providers = (*providers, AuthProvider())
    container = create_container(settings, *providers)

    @asynccontextmanager
    async def lifespan(app):
        try:
            yield
        finally:
            container.close()

    app = FastAPI(title="VaticanDefendor — инвентарь", version="1", lifespan=lifespan,
                  description=DESCRIPTION, openapi_tags=TAGS,
                  swagger_ui_parameters={"docExpansion": "none", "filter": True, "displayRequestDuration": True,
                                         "persistAuthorization": False, "defaultModelsExpandDepth": 0})
    setup_dishka(container, app)
    app.state.settings = settings
    app.include_router(router)
    if settings.mock_payments_enabled:
        from .mock_payments import router as mock_router
        app.include_router(mock_router)
    install_error_handling(app)
    configure_openapi(app)
    return app
