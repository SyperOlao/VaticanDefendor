from contextlib import asynccontextmanager
from fastapi import FastAPI
from dishka.integrations.fastapi import setup_dishka
from ..bootstrap.di import create_container
from ..bootstrap.settings import Settings
from .routes import router
from .reports import router as reports_router
from .errors import install_error_handling
from .documentation import DESCRIPTION, TAGS, install_openapi


def create_app(settings=None):
    settings=settings or Settings.from_environment()
    container=create_container(settings)
    @asynccontextmanager
    async def lifespan(app):
        try:yield
        finally:container.close()
    app=FastAPI(title='VaticanDefendor — аналитика',version='1',lifespan=lifespan,
        description=DESCRIPTION,openapi_tags=TAGS,
        swagger_ui_parameters={'docExpansion':'none','filter':True,'displayRequestDuration':True,
                               'defaultModelsExpandDepth':1,'persistAuthorization':False})
    app.state.settings=settings
    setup_dishka(container,app)
    app.include_router(router)
    app.include_router(reports_router)
    install_error_handling(app)
    install_openapi(app)
    return app
