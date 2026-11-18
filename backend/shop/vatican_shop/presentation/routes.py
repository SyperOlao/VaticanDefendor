from uuid import UUID
from dishka.integrations.fastapi import FromDishka, inject_sync
from fastapi import APIRouter, Depends, Query, Request, Response
from starlette.concurrency import run_in_threadpool
from fastapi.responses import JSONResponse
from .access import bearer_token, player
from .schemas import OfferCreate, OfferUpdate, Purchase, RetryPurchase
from ..application.content import AdminCatalog
from ..application.media import AdminMedia
from ..application.errors import ApplicationError
from ..domain.media import MAX_IMAGE_BYTES
from ..application.use_cases import ShopUseCases
from ..application.health import HealthUseCases
from ..application.operations import AdminPurchases
from ..application.catalog import ReceiptStatus

router = APIRouter()


@router.get("/health/live")
def live():
    return {"status": "live"}


@router.get("/health/ready")
@inject_sync
def ready(use_case: FromDishka[HealthUseCases]):
    return use_case.ready()


@router.get("/v1/shop")
@inject_sync
def catalog(use_case: FromDishka[ShopUseCases], account_id: UUID = Depends(player)):
    return use_case.get_catalog(account_id)


@router.post("/v1/purchases")
@inject_sync
def purchase(body: Purchase, use_case: FromDishka[ShopUseCases], account_id: UUID = Depends(player)):
    result = use_case.purchase(account_id, body.operationId, body.offerId, body.catalogVersion)
    return JSONResponse(result, status_code=202 if result["status"] == "Pending" else 409 if result["status"] == "Rejected" else 200)


@router.get("/v1/purchases/{operation_id}")
@inject_sync
def receipt(operation_id: UUID, use_case: FromDishka[ShopUseCases], account_id: UUID = Depends(player)):
    return use_case.receipt(account_id, operation_id)


@router.get("/v1/purchases")
@inject_sync
def history(use_case: FromDishka[ShopUseCases], account_id: UUID = Depends(player),
            limit: int = Query(default=20, ge=1, le=100), before: UUID | None = None):
    return use_case.history(account_id, limit, before)


@router.get("/v1/admin/shop/offers", tags=["admin"])
@inject_sync
def admin_offers(use_case: FromDishka[AdminCatalog], token: str = Depends(bearer_token),
                 limit: int = Query(default=50, ge=1, le=100), after: str | None = Query(default=None, max_length=64),
                 includeDeleted: bool = False):
    return use_case.list(token, limit, after, includeDeleted)


@router.get("/v1/admin/shop/offers/{offer_id}", tags=["admin"])
@inject_sync
def admin_offer(offer_id: str, use_case: FromDishka[AdminCatalog], token: str = Depends(bearer_token)):
    return use_case.get(token, offer_id)


@router.post("/v1/admin/shop/offers", status_code=201, tags=["admin"])
@inject_sync
def create_offer(body: OfferCreate, use_case: FromDishka[AdminCatalog], token: str = Depends(bearer_token)):
    return use_case.create(token, body.model_dump())


@router.put("/v1/admin/shop/offers/{offer_id}", tags=["admin"])
@inject_sync
def update_offer(offer_id: str, body: OfferUpdate, use_case: FromDishka[AdminCatalog], token: str = Depends(bearer_token)):
    content = body.model_dump(exclude={"expectedRevision"})
    if "imageKey" not in body.model_fields_set:
        content.pop("imageKey")
    return use_case.update(token, offer_id, body.expectedRevision, content)


@router.delete("/v1/admin/shop/offers/{offer_id}", status_code=204, tags=["admin"])
@inject_sync
def delete_offer(offer_id: str, use_case: FromDishka[AdminCatalog],
                 expectedRevision: int = Query(ge=1), token: str = Depends(bearer_token)):
    use_case.delete(token, offer_id, expectedRevision)
    return Response(status_code=204)


@router.post("/v1/admin/shop/images", status_code=201, tags=["admin"])
@inject_sync
async def upload_image(request: Request, use_case: FromDishka[AdminMedia], token: str = Depends(bearer_token)):
    await run_in_threadpool(use_case.authorize, token)
    # Limit the stream before buffering; content is a raw image, not multipart or JSON.
    content = bytearray()
    async for chunk in request.stream():
        if len(content) + len(chunk) > MAX_IMAGE_BYTES:
            raise ApplicationError(413, "Image exceeds 8 MiB")
        content.extend(chunk)
    return await run_in_threadpool(use_case.upload, token, bytes(content), request.headers.get("content-type", ""))


@router.get("/v1/admin/shop/purchases", tags=["admin"])
@inject_sync
def admin_purchases(use_case: FromDishka[AdminPurchases], token: str = Depends(bearer_token),
                    limit: int = Query(default=50, ge=1, le=100),
                    before: str | None = Query(default=None, max_length=256),
                    status: ReceiptStatus | None = None, accountId: UUID | None = None):
    return use_case.list(token, limit, before, status, accountId)


@router.get("/v1/admin/shop/accounts/{account_id}/purchases/{operation_id}", tags=["admin"])
@inject_sync
def admin_purchase(account_id: UUID, operation_id: UUID, use_case: FromDishka[AdminPurchases],
                   token: str = Depends(bearer_token)):
    return use_case.get(token, account_id, operation_id)


@router.get("/v1/admin/shop/accounts/{account_id}/purchases/{operation_id}/events", tags=["admin"])
@inject_sync
def purchase_events(account_id: UUID, operation_id: UUID, use_case: FromDishka[AdminPurchases],
                    token: str = Depends(bearer_token), after: int = Query(default=0, ge=0, le=9_223_372_036_854_775_807),
                    limit: int = Query(default=50, ge=1, le=100)):
    return use_case.events(token, account_id, operation_id, after, limit)


@router.post("/v1/admin/shop/accounts/{account_id}/purchases/{operation_id}/retry", tags=["admin"])
@inject_sync
def retry_purchase(account_id: UUID, operation_id: UUID, body: RetryPurchase,
                   use_case: FromDishka[AdminPurchases], token: str = Depends(bearer_token)):
    return use_case.retry(token, account_id, operation_id, body.expectedVersion, body.requestKey, body.reason)
