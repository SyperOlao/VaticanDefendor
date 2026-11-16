from contextlib import asynccontextmanager

from dishka.integrations.fastapi import setup_dishka
from fastapi import FastAPI

from ..application.ports import PasswordService
from ..bootstrap.di import create_auth_container
from ..persistence.postgres.database import Database
from .errors import ERROR_RESPONSES, install_error_handling
from .routes import router
from ..bootstrap.settings import AuthSettings


def create_app(settings: AuthSettings | None = None) -> FastAPI:
    settings = settings or AuthSettings.from_environment()
    container = create_auth_container(settings)

    @asynccontextmanager
    async def lifespan(app):
        try:
            await container.get(Database)
            await container.get(PasswordService)
            yield
        finally:
            await container.close()

    app = FastAPI(title="VaticanDefendor Auth", version="1", lifespan=lifespan, responses=ERROR_RESPONSES)
    setup_dishka(container, app)
    install_error_handling(app)
    app.include_router(router)
    return app
