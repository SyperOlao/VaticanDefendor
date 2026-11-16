"""HTTP adapters and public catalog validation compatibility imports."""
from uuid import UUID

from fastapi import Depends, Request
from fastapi.exception_handlers import http_exception_handler, request_validation_exception_handler
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse
from pydantic import BaseModel, ConfigDict, Field
from starlette.exceptions import HTTPException as StarletteHTTPException

from .catalogs import build_catalog, validate_catalog


class Purchase(BaseModel):
    model_config = ConfigDict(extra="forbid")
    operationId: UUID
    offerId: str = Field(pattern=r"^[a-z0-9_.-]{1,64}$")
    catalogVersion: str = Field(min_length=1, max_length=64)


def is_shop_request(request: Request):
    return request.url.path in ("/v1/shop", "/v1/purchases") or request.url.path.startswith("/v1/purchases/")


def install_shop(app, use_cases, account_from_bearer):
    @app.exception_handler(RequestValidationError)
    async def invalid_request(request, error):
        if is_shop_request(request):
            return JSONResponse(status_code=400, content={"code": "InvalidRequest"})
        return await request_validation_exception_handler(request, error)

    @app.exception_handler(StarletteHTTPException)
    async def shop_error(request, error):
        if is_shop_request(request):
            code = "Unauthorized" if error.status_code == 401 else "ServiceError"
            content = error.detail if isinstance(error.detail, dict) else {"code": code}
            return JSONResponse(status_code=error.status_code, content=content, headers=error.headers)
        return await http_exception_handler(request, error)

    @app.exception_handler(Exception)
    async def internal_error(request, error):
        # No purchase outcome is implied by a transport/service failure.
        return JSONResponse(status_code=500, content={"code": "InternalError"})

    @app.get("/v1/shop")
    def get_catalog(account_id=Depends(account_from_bearer)):
        return use_cases.get_catalog(account_id)

    @app.get("/v1/inventory")
    def get_inventory(account_id=Depends(account_from_bearer)):
        return use_cases.inventory(account_id)

    @app.post("/v1/purchases")
    def purchase(body: Purchase, account_id=Depends(account_from_bearer)):
        receipt = use_cases.purchase(account_id, body.operationId, body.offerId, body.catalogVersion)
        return JSONResponse(status_code=409 if receipt.get("status") == "Rejected" else 200, content=receipt)

    @app.get("/v1/purchases/{operation_id}")
    def purchase_receipt(operation_id: UUID, account_id=Depends(account_from_bearer)):
        return use_cases.receipt(account_id, operation_id)
