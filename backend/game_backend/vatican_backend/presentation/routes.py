from ..application.cases import CaseUseCases
from .schemas import OpenCase
from ..domain.cases import OpeningStatus
from pathlib import Path as FilePath
from uuid import UUID
from typing import Annotated
from . import openapi_examples as ex
from dishka.integrations.fastapi import FromDishka, inject_sync
from fastapi import APIRouter, Depends, Path, Query, Request, Response
from fastapi.responses import FileResponse, JSONResponse
from fastapi.security import HTTPBearer,APIKeyHeader
from ..application.commands import Assignment, Checkpoint, RunResult as ResultCommand, ServerRegistration
from ..application.runs import RunUseCases
from ..application.profiles import ProfileUseCases, HealthUseCases
from ..application.weapons import WeaponDefinitionsUseCases
from ..application.content import ContentUseCases
from ..application.access import AuthenticatePlayer
from ..application.energy import EnergyUseCases
from ..application.progression import ProgressionUseCases
from ..application.level_rewards import LevelRewardsUseCases
from ..application.catalog import LevelClaimStatus
from ..domain.progression import RunStats
from .schemas import ServerReady, AssignmentAck, CheckpointPublish, RunResult, RenameProfile, UpdateProfile, StartRun, ClaimLevelReward
from .content import Publication
from .schemas import StartLocalRun,FinishLocalRun,BuyPrestigeUpgrade
from ..application.prestige import PrestigeUseCases
from ..application.local_runs import LocalRunUseCases
from ..domain.local_runs import LocalResult

router = APIRouter()
bearer = HTTPBearer(auto_error=False,description="Access token сервиса auth. Вставьте только токен, без слова Bearer; аккаунт определяется сервером.")
server_secret = APIKeyHeader(name='X-Server-Secret',scheme_name='DedicatedServerSecret',auto_error=False,
    description='Секрет конкретного Unity dedicated-процесса из VD_SERVER_POOL_JSON. Не токен игрока.')
content_secret = APIKeyHeader(name='X-Content-Admin-Secret',scheme_name='ContentPublisherSecret',auto_error=False,
    description='Операторский секрет публикации VD_CONTENT_ADMIN_SECRET. Не секрет игрового сервера.')
RunId=Annotated[UUID,Path(description='runId из ответа запуска/назначения. Доступ игрока проверяется по токену.',examples=[ex.RUN])]
ServerId=Annotated[UUID,Path(description='ID настроенного Unity dedicated-слота; секрет должен принадлежать этому serverId.',examples=['bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'])]
ClaimId=Annotated[UUID,Path(description='claimId из /v1/level-rewards или progression.availableRewards.',examples=[ex.CLAIM])]
BundleId=Annotated[UUID,Path(description='rewardBundleId из подарка/кейса. Чтение определения не выдаёт награду.',examples=[ex.BUNDLE])]
CaseId=Annotated[UUID,Path(description='caseId из публичного каталога /v1/cases.',examples=[ex.CASE])]
OpeningId=Annotated[UUID,Path(description='openingId из ответа открытия кейса.',examples=[ex.OPENING])]
PurchaseId=Annotated[UUID,Path(description='purchaseId покупки улучшения из POST purchases или pendingPurchase каталога.',examples=[ex.PURCHASE])]
ReleaseId=Annotated[str,Path(description='ID опубликованного ContentRelease. Используйте реальный релиз вашего развёртывания.',examples=[ex.RELEASE])]


@router.get('/v1/reward-bundles/{bundle_id}')
@inject_sync
def reward_bundle(bundle_id: BundleId, service: FromDishka[LevelRewardsUseCases]):
    return service.bundle(bundle_id)


@router.post('/v1/local-runs',dependencies=[Depends(bearer)])
@inject_sync
def start_local(body: StartLocalRun,request: Request,service: FromDishka[LocalRunUseCases],access: FromDishka[AuthenticatePlayer]):
    account=access.execute(request.headers.get('authorization')).account_id
    return service.start(account,body.requestKey,body.policyId)


@router.get('/v1/local-runs/active',dependencies=[Depends(bearer)])
@inject_sync
def active_local(request: Request,service: FromDishka[LocalRunUseCases],access: FromDishka[AuthenticatePlayer]):
    return service.active(access.execute(request.headers.get('authorization')).account_id)


@router.get('/v1/local-runs/{run_id}',dependencies=[Depends(bearer)])
@inject_sync
def local_status(run_id: RunId,request: Request,service: FromDishka[LocalRunUseCases],access: FromDishka[AuthenticatePlayer]):
    return service.get(access.execute(request.headers.get('authorization')).account_id,run_id)


@router.post('/v1/local-runs/{run_id}/finish',dependencies=[Depends(bearer)])
@inject_sync
def finish_local(body: FinishLocalRun,run_id: RunId,request: Request,service: FromDishka[LocalRunUseCases],access: FromDishka[AuthenticatePlayer]):
    account=access.execute(request.headers.get('authorization')).account_id
    result=LocalResult(body.sessionId,body.policyId,body.contentReleaseId,body.rulesetId,body.mapId,body.difficulty,
        body.outcome,body.churchLevel,body.durationMs,RunStats(tuple(body.stats.heroKills.items()),
            tuple(body.stats.towerKills.items()),tuple(body.stats.buildingUpgrades.items())))
    return service.finish(account,run_id,result)


@router.post('/v1/local-runs/{run_id}/abandon',dependencies=[Depends(bearer)])
@inject_sync
def abandon_local(run_id: RunId,request: Request,service: FromDishka[LocalRunUseCases],access: FromDishka[AuthenticatePlayer]):
    return service.abandon(access.execute(request.headers.get('authorization')).account_id,run_id)

@router.get('/v1/prestige-upgrades',dependencies=[Depends(bearer)])
@inject_sync
def prestige_catalog(request: Request,service: FromDishka[PrestigeUseCases],access: FromDishka[AuthenticatePlayer]):
    return service.catalog(access.execute(request.headers.get('authorization')).account_id)


@router.post('/v1/prestige-upgrades/purchases',dependencies=[Depends(bearer)])
@inject_sync
def buy_prestige(body: BuyPrestigeUpgrade,request: Request,service: FromDishka[PrestigeUseCases],access: FromDishka[AuthenticatePlayer]):
    value=service.buy(access.execute(request.headers.get('authorization')).account_id,body.requestKey,body.rulesetId,body.code,body.expectedLevel)
    return JSONResponse(status_code=200 if value['status'] in ('COMPLETED','REJECTED') else 202,content=value)


@router.get('/v1/prestige-upgrades/purchases/{purchase_id}',dependencies=[Depends(bearer)])
@inject_sync
def prestige_status(purchase_id: PurchaseId,request: Request,service: FromDishka[PrestigeUseCases],access: FromDishka[AuthenticatePlayer]):
    return service.get(access.execute(request.headers.get('authorization')).account_id,purchase_id)


def registration(body):
    return ServerRegistration(body.contentReleaseId, body.protocolVersion, body.transport, body.rulesetId,body.prestigeSchemaVersion)

def assignment(body):
    return Assignment(body.runId, body.generation)

@router.get("/health/live")
def live():
    return {"status": "live"}

@router.get("/health/ready")
@inject_sync
def ready(*, use_case: FromDishka[HealthUseCases]):
    return use_case.ready()


@router.post("/internal/servers/{server_id}/ready")
@inject_sync
def server_ready(body: ServerReady, server_id: ServerId, x_server_secret: str | None=Depends(server_secret), *, use_case: FromDishka[RunUseCases]):
    return use_case.register_server(server_id, x_server_secret, registration(body))

@router.get("/internal/servers/{server_id}/assignment")
@inject_sync
def server_assignment(server_id: ServerId, x_server_secret: str | None=Depends(server_secret), *, use_case: FromDishka[RunUseCases]):
    return use_case.server_assignment(server_id, x_server_secret)

@router.post("/internal/servers/{server_id}/ack")
@inject_sync
def server_ack(body: AssignmentAck, server_id: ServerId, x_server_secret: str | None=Depends(server_secret), *, use_case: FromDishka[RunUseCases]):
    return use_case.acknowledge(server_id, x_server_secret, assignment(body))

@router.post("/internal/servers/{server_id}/heartbeat")
@inject_sync
def server_heartbeat(body: AssignmentAck, server_id: ServerId, x_server_secret: str | None=Depends(server_secret), *, use_case: FromDishka[RunUseCases]):
    return use_case.heartbeat(server_id, x_server_secret, assignment(body))

@router.post("/internal/servers/{server_id}/checkpoint")
@inject_sync
def publish_checkpoint(body: CheckpointPublish, server_id: ServerId, x_server_secret: str | None=Depends(server_secret), *, use_case: FromDishka[RunUseCases]):
    command = Checkpoint(body.runId, body.generation, body.schemaVersion, body.simulationTick, body.snapshotJson, body.sha256)
    return use_case.publish_checkpoint(server_id, x_server_secret, command)

@router.post("/internal/servers/{server_id}/recover")
@inject_sync
def recover_checkpoint(body: ServerReady, server_id: ServerId, x_server_secret: str | None=Depends(server_secret), *, use_case: FromDishka[RunUseCases]):
    result = use_case.recover(server_id, x_server_secret, registration(body))
    if result.get('recoveryPending'):
        return JSONResponse(status_code=202, content=result)
    return result

@router.post("/internal/servers/{server_id}/abandon")
@inject_sync
def abandon_run(body: AssignmentAck, server_id: ServerId, x_server_secret: str | None=Depends(server_secret), *, use_case: FromDishka[RunUseCases]):
    return use_case.abandon(server_id, x_server_secret, assignment(body))

@router.post("/internal/servers/{server_id}/results")
@inject_sync
def apply_result(body: RunResult, server_id: ServerId, x_server_secret: str | None=Depends(server_secret), *, use_case: FromDishka[RunUseCases]):
    command = ResultCommand(body.runId, body.generation, body.accountId, body.contentReleaseId, body.outcome, body.churchLevel,
        RunStats(tuple(body.stats.heroKills.items()), tuple(body.stats.towerKills.items()), tuple(body.stats.buildingUpgrades.items())),
        body.schemaVersion, body.serverId, body.rulesetId, body.statistics.model_dump() if body.statistics is not None else None)
    return use_case.apply_result(server_id, x_server_secret, command)



@router.get("/v1/profile", dependencies=[Depends(bearer)])
@inject_sync
def profile(request: Request, use_case: FromDishka[ProfileUseCases], access: FromDishka[AuthenticatePlayer]):
    principal = access.execute(request.headers.get("authorization"))
    return use_case.get(principal.account_id)


@router.get('/v1/profile/display-names', dependencies=[Depends(bearer)])
@inject_sync
def profile_display_names(request: Request, use_case: FromDishka[ProfileUseCases],
                          access: FromDishka[AuthenticatePlayer],
                          account_ids: Annotated[list[UUID], Query(alias='accountId', min_length=1, max_length=100,
                              description='UUID участников из рейтинга. Повторите параметр для каждого игрока.')]):
    access.execute(request.headers.get('authorization'))
    return use_case.display_names(account_ids)


@router.put("/v1/profile", dependencies=[Depends(bearer)])
@inject_sync
def rename_profile(body: RenameProfile, request: Request, use_case: FromDishka[ProfileUseCases],
                   access: FromDishka[AuthenticatePlayer]):
    principal = access.execute(request.headers.get("authorization"))
    return use_case.update(principal.account_id, body.expectedVersion, body.model_dump(exclude_unset=True,exclude={'expectedVersion'}))


@router.get('/v1/profile/avatars',dependencies=[Depends(bearer)])
@inject_sync
def profile_avatars(request: Request,use_case: FromDishka[ProfileUseCases],access: FromDishka[AuthenticatePlayer]):
    access.execute(request.headers.get('authorization'))
    return use_case.available_avatars()


@router.patch("/v1/profile", dependencies=[Depends(bearer)])
@inject_sync
def update_profile(body: UpdateProfile, request: Request, use_case: FromDishka[ProfileUseCases],
                   access: FromDishka[AuthenticatePlayer]):
    principal = access.execute(request.headers.get("authorization"))
    return use_case.update(principal.account_id,body.expectedVersion,
                           body.model_dump(exclude_unset=True,exclude={'expectedVersion'}))


@router.post("/v1/runs", status_code=202, dependencies=[Depends(bearer)])
@inject_sync
def start_run(body: StartRun, request: Request, use_case: FromDishka[RunUseCases], access: FromDishka[AuthenticatePlayer]):
    principal = access.execute(request.headers.get("authorization"))
    return use_case.assign(principal.account_id, respond_async=True, request_key=body.requestKey)


@router.get("/v1/runs/{run_id}", dependencies=[Depends(bearer)])
@inject_sync
def run_status(run_id: RunId, request: Request, use_case: FromDishka[RunUseCases], access: FromDishka[AuthenticatePlayer]):
    principal = access.execute(request.headers.get("authorization"))
    return use_case.assignment_status(run_id, principal.account_id)


@router.post("/v1/runs/{run_id}/grant", dependencies=[Depends(bearer)])
@inject_sync
def renew_grant(run_id: RunId, request: Request, use_case: FromDishka[RunUseCases], access: FromDishka[AuthenticatePlayer]):
    principal = access.execute(request.headers.get("authorization"))
    return use_case.renew_grant(run_id, principal.account_id)


@router.get("/v1/weapon-configurations")
@inject_sync
def weapon_definitions(use_case: FromDishka[WeaponDefinitionsUseCases]):
    return use_case.get()


@router.get("/v1/weapons/media/{configuration_id}.png", include_in_schema=False)
@inject_sync
def weapon_image(configuration_id: str, use_case: FromDishka[WeaponDefinitionsUseCases]):
    model = use_case.media_model(configuration_id)
    image = FilePath(__file__).resolve().parents[2] / "media" / f"{model}.png"
    return FileResponse(image, media_type="image/png", headers={"Cache-Control": "public, max-age=3600"})


@router.put("/internal/content/{release_id}")
@inject_sync
def publish(release_id: ReleaseId, body: Publication, use_case: FromDishka[ContentUseCases],
            x_content_admin_secret: str | None = Depends(content_secret)):
    return use_case.publish(release_id, body.schemaVersion, body.payloadJson, body.payloadSha256,
                            body.releaseId, x_content_admin_secret)


@router.get("/v1/content/{release_id}")
@inject_sync
def get_content(release_id: ReleaseId, response: Response, use_case: FromDishka[ContentUseCases]):
    result = use_case.get(release_id)
    response.headers["Cache-Control"] = "public, max-age=31536000, immutable"
    response.headers["ETag"] = '"' + result["payloadSha256"] + '"'
    return result


@router.get("/v1/energy", dependencies=[Depends(bearer)])
@inject_sync
def energy(request: Request, use_case: FromDishka[EnergyUseCases], access: FromDishka[AuthenticatePlayer]):
    principal = access.execute(request.headers.get("authorization"))
    return use_case.get(principal.account_id)


@router.get("/v1/progression", dependencies=[Depends(bearer)])
@inject_sync
def progression(request: Request, use_case: FromDishka[ProgressionUseCases], access: FromDishka[AuthenticatePlayer]):
    principal = access.execute(request.headers.get("authorization"))
    return use_case.get(principal.account_id)


@router.get("/v1/level-rewards", dependencies=[Depends(bearer)])
@inject_sync
def list_level_rewards(request: Request, use_case: FromDishka[LevelRewardsUseCases], access: FromDishka[AuthenticatePlayer],
                       limit: int = Query(default=50, ge=1, le=100,description="Количество записей на странице, 1–100."), offset: int = Query(default=0, ge=0, le=100000,description="Сколько записей пропустить. Для следующей страницы увеличьте на limit.")):
    principal = access.execute(request.headers.get("authorization"))
    return use_case.list(principal.account_id, limit, offset)


@router.get("/v1/level-rewards/{claim_id}", dependencies=[Depends(bearer)])
@inject_sync
def get_level_reward(claim_id: ClaimId, request: Request, use_case: FromDishka[LevelRewardsUseCases], access: FromDishka[AuthenticatePlayer]):
    principal = access.execute(request.headers.get("authorization"))
    return use_case.get(principal.account_id, claim_id)


@router.post("/v1/level-rewards/{claim_id}/claim", dependencies=[Depends(bearer)])
@inject_sync
def claim_level_reward(claim_id: ClaimId, request: Request, use_case: FromDishka[LevelRewardsUseCases], access: FromDishka[AuthenticatePlayer],
                       body: ClaimLevelReward | None = None):
    principal = access.execute(request.headers.get("authorization"))
    result = use_case.claim(principal.account_id, claim_id)
    return JSONResponse(result, status_code=200 if result['status'] == LevelClaimStatus.CLAIMED else 202)


@router.get('/v1/cases')
@inject_sync
def case_catalog(use_case: FromDishka[CaseUseCases], limit: int = Query(default=50,ge=1,le=100,description="Количество записей на странице, 1–100."),
                 offset: int = Query(default=0,ge=0,le=100000,description="Сколько записей пропустить. Для следующей страницы увеличьте на limit.")):
    return use_case.catalog(limit,offset)


@router.post('/v1/cases/{case_id}/open', dependencies=[Depends(bearer)])
@inject_sync
def open_case(case_id: CaseId, body: OpenCase, request: Request, use_case: FromDishka[CaseUseCases],
              access: FromDishka[AuthenticatePlayer]):
    principal = access.execute(request.headers.get('authorization'))
    result = use_case.open(principal.account_id,case_id,body.requestKey,body.paymentMode)
    return JSONResponse(result,status_code=200 if result['status'] in (OpeningStatus.COMPLETED,OpeningStatus.REJECTED) else 202)


@router.get('/v1/case-openings/{opening_id}', dependencies=[Depends(bearer)])
@inject_sync
def case_opening(opening_id: OpeningId, request: Request, use_case: FromDishka[CaseUseCases], access: FromDishka[AuthenticatePlayer]):
    principal = access.execute(request.headers.get('authorization'))
    return use_case.get(principal.account_id,opening_id)
