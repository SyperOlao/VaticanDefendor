from contextlib import asynccontextmanager
from fastapi import FastAPI
from dishka.integrations.fastapi import setup_dishka
from ..bootstrap.settings import Settings
from ..bootstrap.di import create_container
from ..application.ports import AuthGateway
from .routes import router
from .errors import install_error_handling
from .ingress import MessageIngressMiddleware
from .openapi import DESCRIPTION, TAGS, install_openapi


def create_app(settings=None, *, providers=(), auth_gateway=None):
    settings = settings or Settings.from_environment()
    container = create_container(settings, *providers, auth_gateway=auth_gateway)

    @asynccontextmanager
    async def lifespan(app):
        try:
            # Validate channel/TLS configuration at startup without waiting for auth.
            container.get(AuthGateway)
            yield
        finally:
            container.close()

    app = FastAPI(
        title="VaticanDefendor — рейтинг игроков", version="1", lifespan=lifespan,
        description=DESCRIPTION, openapi_tags=TAGS,
        swagger_ui_parameters={"filter": True, "displayRequestDuration": True,
                               "docExpansion": "list", "defaultModelsExpandDepth": 1},
    )
    setup_dishka(container, app)
    app.state.settings = settings
    app.include_router(router)
    install_openapi(app)
    app.add_middleware(MessageIngressMiddleware, service_secret=settings.service_secret)
    install_error_handling(app)
    return app
