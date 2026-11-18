from contextlib import asynccontextmanager
from fastapi import FastAPI
from dishka.integrations.fastapi import setup_dishka
from ..bootstrap.settings import Settings
from ..bootstrap.di import create_container
from ..application.ports import AuthGateway
from .routes import router
from .errors import install_error_handling
from .documentation import DESCRIPTION, TAGS, preserve_examples
from .message_examples import install_message_examples


def create_app(settings=None, *, providers=(), auth_gateway=None):
    settings = settings or Settings.from_environment()
    container = create_container(settings, *providers)

    @asynccontextmanager
    async def lifespan(app):
        try:
            yield
        finally:
            container.close()

    app = FastAPI(title="VaticanDefendor — кошелёк", version="1", lifespan=lifespan, description=DESCRIPTION, openapi_tags=TAGS,
                  swagger_ui_parameters={"docExpansion":"none", "filter":True, "persistAuthorization":False})
    setup_dishka(container, app)
    app.state.settings = settings
    app.state.auth = auth_gateway or container.get(AuthGateway)
    app.include_router(router)
    preserve_examples(app)
    install_message_examples(app)
    install_error_handling(app)
    return app
