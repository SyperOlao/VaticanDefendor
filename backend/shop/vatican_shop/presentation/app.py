from contextlib import asynccontextmanager
from fastapi import FastAPI
from dishka.integrations.fastapi import setup_dishka
from dishka import Provider, Scope, provide
from ..bootstrap.settings import Settings
from ..bootstrap.di import create_container
from ..application.ports import AuthGateway
from .routes import router
from .errors import install_error_handling
from .documentation import install_documentation


def create_app(settings=None, *, providers=(), auth_gateway=None):
    settings = settings or Settings.from_environment()
    if auth_gateway is not None:
        class AuthOverride(Provider):
            @provide(scope=Scope.APP, provides=AuthGateway)
            def auth(self):
                return auth_gateway
        providers = (*providers, AuthOverride())
    container = create_container(settings, *providers)

    @asynccontextmanager
    async def lifespan(app):
        try:
            yield
        finally:
            container.close()

    app = FastAPI(title="VaticanDefendor Shop", version="1", lifespan=lifespan)
    setup_dishka(container, app)
    app.state.settings = settings
    app.state.auth = container.get(AuthGateway)
    app.include_router(router)
    if settings.mock_payments_enabled:
        from .mock_payments import router as mock_router
        app.include_router(mock_router)
    install_error_handling(app)
    install_documentation(app, settings)
    return app
