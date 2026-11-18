from contextlib import asynccontextmanager

from dishka.integrations.fastapi import setup_dishka
from fastapi import FastAPI

from ..application.ports import PasswordService
from ..bootstrap.di import create_auth_container
from ..persistence.postgres.database import Database
from .documentation import TAG_METADATA, api_description
from .errors import install_error_handling
from .routes import router
from .openapi import preserve_openapi_null_examples
from ..bootstrap.settings import AuthSettings
from ..bootstrap.grpc import AuthGrpcSettings


def create_app(settings: AuthSettings | None = None, *, grpc_settings: AuthGrpcSettings | None = None) -> FastAPI:
    settings = settings or AuthSettings.from_environment()
    grpc_settings = grpc_settings or AuthGrpcSettings.from_environment()
    container = create_auth_container(settings)

    @asynccontextmanager
    async def lifespan(app):
        grpc_server = None
        try:
            await container.get(Database)
            await container.get(PasswordService)
            if grpc_settings.enabled:
                from .grpc import start_auth_server
                grpc_server, port = await start_auth_server(container, grpc_settings)
                app.state.auth_grpc_server, app.state.auth_grpc_port = grpc_server, port
            yield
        finally:
            try:
                if grpc_server is not None:
                    await grpc_server.stop(2)
            finally:
                await container.close()

    app = FastAPI(
        title="VaticanDefendor — аккаунты и доступ", version="1", lifespan=lifespan,
        description=api_description(settings.policy), openapi_tags=TAG_METADATA,
        swagger_ui_parameters={"displayRequestDuration": True, "docExpansion": "none",
                               "defaultModelsExpandDepth": 1, "filter": True},
    )
    setup_dishka(container, app)
    install_error_handling(app)
    app.include_router(router)
    preserve_openapi_null_examples(app)
    return app
