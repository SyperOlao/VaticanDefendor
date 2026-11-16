"""Composition root and HTTP adapters. Database access lives in repositories."""
from contextlib import asynccontextmanager
from functools import partial
from types import SimpleNamespace
from uuid import UUID

from fastapi import FastAPI, Header, HTTPException, Path
from fastapi.exception_handlers import http_exception_handler
from fastapi.responses import JSONResponse
from pydantic import BaseModel, ConfigDict, Field
from sqlalchemy.exc import SQLAlchemyError, TimeoutError as OrmPoolTimeout

from .application.commands import Assignment, Checkpoint, RunResult as ResultCommand, ServerRegistration
from .application.content import ContentUseCases
from .application.errors import ApplicationError
from .application.profiles import HealthUseCases, ProfileUseCases
from .application.runs import RunUseCases
from .application.shop import ShopUseCases
from .application.weapons import WeaponUseCases
from .catalogs import build_catalog
from .content import install_content
from .migrate import SCHEMA_REVISION
from .persistence.postgres import Database
from .persistence.postgres.unit_of_work import UnitOfWork
from .settings import Settings
from .shop import is_shop_request


class StrictBody(BaseModel):
    model_config = ConfigDict(extra="forbid")


class ServerReady(StrictBody):
    contentReleaseId: str = Field(min_length=1, max_length=128)
    protocolVersion: int = Field(ge=1)
    transport: str = "udp-loopback"


class AssignmentAck(StrictBody):
    runId: UUID
    generation: int = Field(ge=1)


class CheckpointPublish(StrictBody):
    runId: UUID
    generation: int = Field(ge=1)
    schemaVersion: int = Field(ge=4, le=4)
    simulationTick: int = Field(ge=0)
    snapshotJson: str = Field(min_length=2, max_length=8_000_000)
    sha256: str = Field(pattern="^[0-9a-f]{64}$")


class RunResult(StrictBody):
    runId: UUID
    accountId: UUID
    contentReleaseId: str = Field(min_length=1, max_length=128)
    generation: int = Field(ge=1)
    outcome: str = Field(pattern="^(victory|defeat)$")
    churchLevel: int = Field(ge=1, le=1000)


def create_app(settings: Settings | None = None) -> FastAPI:
    settings = settings or Settings.from_environment()
    # Keep this lookup dynamic for catalog deployment/validation overrides.
    from .weapons import LoadCatalog
    weapon_catalog = LoadCatalog()
    database = Database(settings.database_url, settings.pool_size)
    uow = partial(UnitOfWork, database)
    weapons = WeaponUseCases(uow, weapon_catalog)
    use_cases = SimpleNamespace(
        profiles=ProfileUseCases(uow), weapons=weapons,
        content=ContentUseCases(uow, settings.content_admin_secret),
        shop=ShopUseCases(uow, build_catalog(settings.shop_catalog, weapon_catalog), weapons),
        runs=RunUseCases(uow, settings, weapons), health=HealthUseCases(uow, SCHEMA_REVISION),
    )

    @asynccontextmanager
    async def lifespan(app):
        try:
            weapons.register_catalog()
            yield
        finally:
            database.close()

    app = FastAPI(title="VaticanDefendor Backend", version="1", lifespan=lifespan)
    app.state.database = database
    app.state.use_cases = use_cases

    @app.exception_handler(OrmPoolTimeout)
    async def pool_unavailable(request, error):
        return JSONResponse(status_code=503, content={"code": "ServiceUnavailable", "detail": "Database capacity unavailable"},
                            headers={"Retry-After": "1"})

    @app.exception_handler(ApplicationError)
    async def application_error(request, error):
        if is_shop_request(request):
            code = "Unauthorized" if error.status_code == 401 else "ServiceError"
            detail = error.detail if isinstance(error.detail, dict) else {"code": code}
            return JSONResponse(status_code=error.status_code, content=detail, headers=error.headers)
        return await http_exception_handler(request, HTTPException(error.status_code, error.detail, error.headers))

    def registration(body):
        return ServerRegistration(body.contentReleaseId, body.protocolVersion, body.transport)

    def assignment(body):
        return Assignment(body.runId, body.generation)

    @app.get("/health/live")
    def live():
        return {"status": "live"}

    @app.get("/health/ready")
    def ready():
        try:
            return use_cases.health.ready()
        except SQLAlchemyError as error:
            raise HTTPException(503, f"Database is not ready: {error.__class__.__name__}") from None


    @app.post("/internal/servers/{server_id}/ready")
    def server_ready(body: ServerReady, server_id: UUID = Path(), x_server_secret: str | None = Header(default=None)):
        return use_cases.runs.register_server(server_id, x_server_secret, registration(body))

    @app.get("/internal/servers/{server_id}/assignment")
    def server_assignment(server_id: UUID, x_server_secret: str | None = Header(default=None)):
        return use_cases.runs.server_assignment(server_id, x_server_secret)

    @app.post("/internal/servers/{server_id}/ack")
    def server_ack(body: AssignmentAck, server_id: UUID, x_server_secret: str | None = Header(default=None)):
        return use_cases.runs.acknowledge(server_id, x_server_secret, assignment(body))

    @app.post("/internal/servers/{server_id}/heartbeat")
    def server_heartbeat(body: AssignmentAck, server_id: UUID, x_server_secret: str | None = Header(default=None)):
        return use_cases.runs.heartbeat(server_id, x_server_secret, assignment(body))

    @app.post("/internal/servers/{server_id}/checkpoint")
    def publish_checkpoint(body: CheckpointPublish, server_id: UUID, x_server_secret: str | None = Header(default=None)):
        command = Checkpoint(body.runId, body.generation, body.schemaVersion, body.simulationTick, body.snapshotJson, body.sha256)
        return use_cases.runs.publish_checkpoint(server_id, x_server_secret, command)

    @app.post("/internal/servers/{server_id}/recover")
    def recover_checkpoint(body: ServerReady, server_id: UUID, x_server_secret: str | None = Header(default=None)):
        result = use_cases.runs.recover(server_id, x_server_secret, registration(body))
        if result.get("recoveryPending"):
            return JSONResponse(status_code=202, content=result)
        return result

    @app.post("/internal/servers/{server_id}/abandon")
    def abandon_run(body: AssignmentAck, server_id: UUID, x_server_secret: str | None = Header(default=None)):
        return use_cases.runs.abandon(server_id, x_server_secret, assignment(body))

    @app.post("/internal/servers/{server_id}/results")
    def apply_result(body: RunResult, server_id: UUID, x_server_secret: str | None = Header(default=None)):
        command = ResultCommand(body.runId, body.generation, body.accountId, body.contentReleaseId, body.outcome, body.churchLevel)
        return use_cases.runs.apply_result(server_id, x_server_secret, command)


    install_content(app, use_cases.content)
    return app
