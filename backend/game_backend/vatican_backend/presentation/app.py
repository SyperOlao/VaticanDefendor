from contextlib import asynccontextmanager
from fastapi import FastAPI
from dishka.integrations.fastapi import setup_dishka
from ..bootstrap.settings import Settings
from ..bootstrap.di import create_game_container, GameUseCases
from ..application.profiles import ProfileUseCases, HealthUseCases
from ..application.runs import RunUseCases
from ..application.weapons import WeaponDefinitionsUseCases
from ..application.messaging import DeliverRewards
from ..application.energy import EnergyUseCases
from ..application.progression import ProgressionUseCases
from ..application.level_rewards import LevelRewardsUseCases
from ..application.cases import CaseUseCases
from ..application.prestige import PrestigeUseCases
from ..application.local_runs import LocalRunUseCases
from ..persistence.postgres.database import Database
from .routes import router
from .errors import install_error_handling
from .openapi import DESCRIPTION,TAGS,configure_router,install_openapi


def create_app(settings=None, *, auth_gateway=None, inventory_gateway=None, delivery_gateway=None):
    settings = settings or Settings.from_environment()
    container, client = create_game_container(settings, auth_gateway, inventory_gateway, delivery_gateway)

    @asynccontextmanager
    async def lifespan(app):
        try:
            container.get(WeaponDefinitionsUseCases).register_catalog()
            yield
        finally:
            container.close()
            client.close()

    app = FastAPI(title="VaticanDefendor Game Backend", version="1", lifespan=lifespan,
        description=DESCRIPTION,openapi_tags=TAGS,
        swagger_ui_parameters={"docExpansion":"list","defaultModelsExpandDepth":1,
            "filter":True,"displayRequestDuration":True,"persistAuthorization":False})
    setup_dishka(container, app)
    app.state.database = container.get(Database)
    app.state.use_cases = GameUseCases(*(container.get(t) for t in
        (ProfileUseCases, RunUseCases, WeaponDefinitionsUseCases, HealthUseCases, DeliverRewards, EnergyUseCases, ProgressionUseCases, LevelRewardsUseCases, CaseUseCases, LocalRunUseCases, PrestigeUseCases)))
    configure_router(router)
    app.include_router(router)
    install_openapi(app)
    install_error_handling(app)
    return app
