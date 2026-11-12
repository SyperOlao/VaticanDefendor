import hashlib
import json
import re
from copy import deepcopy
from uuid import UUID, uuid4

from fastapi import Depends, HTTPException, Request
from fastapi.exception_handlers import http_exception_handler, request_validation_exception_handler
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse
from pydantic import BaseModel, ConfigDict, Field
from psycopg.types.json import Jsonb
from starlette.exceptions import HTTPException as StarletteHTTPException

from .weapons import EnsureInventory


class Purchase(BaseModel):
    model_config = ConfigDict(extra="forbid")
    operationId: UUID
    offerId: str = Field(pattern=r"^[a-z0-9_.-]{1,64}$")
    catalogVersion: str = Field(min_length=1, max_length=64)


def validate_catalog(catalog):
    """Validate deployment data; account-specific fields are derived by the server."""
    if catalog == {}:
        return {"version": "empty", "offers": []}
    if not isinstance(catalog, dict) or set(catalog) != {"version", "offers"}:
        raise ValueError("Shop catalog requires version and offers")
    if not isinstance(catalog["version"], str) or not re.fullmatch(r"[A-Za-z0-9_.-]{1,64}", catalog["version"]):
        raise ValueError("Invalid catalog version")
    if not isinstance(catalog["offers"], list) or len(catalog["offers"]) > 200:
        raise ValueError("Catalog permits at most 200 offers")
    ids = set()
    required = {"id", "weaponConfigurationId", "title", "price", "purchaseLimit"}
    optional = {"currencyId", "available", "unavailableReason"}
    for offer in catalog["offers"]:
        if not isinstance(offer, dict) or not required <= set(offer) or set(offer) - required - optional:
            raise ValueError("Invalid weapon offer fields")
        if not isinstance(offer["id"], str) or not re.fullmatch(r"[a-z0-9_.-]{1,64}", offer["id"]):
            raise ValueError("Invalid offer identifier")
        if not isinstance(offer["weaponConfigurationId"], str) or not re.fullmatch(r"[a-zA-Z0-9_.-]{1,80}", offer["weaponConfigurationId"]):
            raise ValueError("Invalid weapon configuration identifier")
        if offer["id"] in ids:
            raise ValueError("Duplicate offer ID")
        ids.add(offer["id"])
        if not isinstance(offer["title"], str) or not 1 <= len(offer["title"].strip()) <= 80:
            raise ValueError("Invalid offer title")
        for key in ("price", "purchaseLimit"):
            if type(offer[key]) is not int or not 0 <= offer[key] <= 9_223_372_036_854_775_807:
                raise ValueError("Offer price and purchaseLimit must be nonnegative int64 values")
        if offer.get("currencyId", "prestige") != "prestige":
            raise ValueError("Only prestige is supported")
        if type(offer.get("available", True)) is not bool:
            raise ValueError("Invalid offer availability")
        reason = offer.get("unavailableReason", "")
        if not isinstance(reason, str) or (reason and not re.fullmatch(r"[A-Za-z][A-Za-z0-9]{0,63}", reason)):
            raise ValueError("Invalid unavailableReason")
        if offer.get("available", True) and reason:
            raise ValueError("Available offers cannot have an unavailableReason")
    return catalog


def build_catalog(configured, weapon_catalog):
    configured = deepcopy(validate_catalog(configured))
    definitions = {entry["Id"]: entry for entry in weapon_catalog}
    offers = []
    for offer in configured["offers"]:
        if offer["weaponConfigurationId"] not in definitions:
            raise ValueError("Shop offer references an unknown weapon configuration")
        offers.append({"currencyId": "prestige", "available": True, "unavailableReason": "", **offer})
    configurations = [deepcopy(definitions[key]) for key in sorted({offer["weaponConfigurationId"] for offer in offers})]
    snapshot = {"version": configured["version"], "offers": offers, "configurations": configurations}
    # A forgotten deployment version bump must not allow a purchase at a changed price.
    digest = hashlib.sha256(json.dumps(snapshot, sort_keys=True, separators=(",", ":"),
                                      ensure_ascii=False, allow_nan=False).encode("utf-8")).hexdigest()
    return {"version": "shop-" + digest[:48], "currencyId": "prestige",
            "offers": offers, "configurations": configurations}


def is_shop_request(request: Request):
    return request.url.path in ("/v1/shop", "/v1/purchases") or request.url.path.startswith("/v1/purchases/")


def install_shop(app, connect, account_from_bearer, configured, weapon_catalog):
    catalog = build_catalog(configured, weapon_catalog)
    offers = {offer["id"]: offer for offer in catalog["offers"]}

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

    def purchased_counts(db, account_id):
        return {row["offer_id"]: row["count"] for row in db.execute(
            "SELECT offer_id,count(*) AS count FROM purchase_operations "
            "WHERE account_id=%s AND receipt_json->>'status'='Completed' GROUP BY offer_id",
            (account_id,))}

    @app.get("/v1/shop")
    def get_catalog(account_id=Depends(account_from_bearer)):
        with connect() as db:
            with db.transaction():
                # Same first lock as inventory, run assignment and match rewards.
                db.execute("SELECT id FROM accounts WHERE id=%s FOR UPDATE", (account_id,))
                profile = db.execute("SELECT permanent_currency,wallet_revision FROM profiles WHERE account_id=%s",
                                     (account_id,)).fetchone()
                counts = purchased_counts(db, account_id)
                account_offers = []
                for offer in catalog["offers"]:
                    count = counts.get(offer["id"], 0)
                    reason = ""
                    if not offer["available"]:
                        reason = offer["unavailableReason"] or "OfferUnavailable"
                    elif offer["purchaseLimit"] and count >= offer["purchaseLimit"]:
                        reason = "PurchaseLimitReached"
                    account_offers.append({**offer, "purchasedCount": count,
                                           "available": not reason, "unavailableReason": reason})
        return {**catalog, "offers": account_offers, "walletBalance": profile["permanent_currency"],
                "walletRevision": profile["wallet_revision"]}

    @app.get("/v1/inventory")
    def get_inventory(account_id=Depends(account_from_bearer)):
        # Read legacy non-weapon inventory; weapon purchases never write here.
        with connect() as db:
            rows = db.execute("SELECT item_id,quantity FROM inventory WHERE account_id=%s ORDER BY item_id",
                              (account_id,)).fetchall()
        return {"items": [{"itemId": row["item_id"], "quantity": row["quantity"]} for row in rows]}

    @app.post("/v1/purchases")
    def purchase(body: Purchase, account_id=Depends(account_from_bearer)):
        with connect() as db:
            with db.transaction():
                db.execute("SELECT id FROM accounts WHERE id=%s FOR UPDATE", (account_id,))
                prior = db.execute("SELECT * FROM purchase_operations WHERE account_id=%s AND operation_id=%s",
                                   (account_id, body.operationId)).fetchone()
                if prior:
                    if prior["offer_id"] != body.offerId or prior["catalog_version"] != body.catalogVersion:
                        raise HTTPException(409, {"code": "OperationConflict", "operationId": str(body.operationId)})
                    receipt = {**prior["receipt_json"], "replayed": True}
                else:
                    profile = db.execute("SELECT permanent_currency FROM profiles WHERE account_id=%s FOR UPDATE",
                                         (account_id,)).fetchone()
                    offer = offers.get(body.offerId)
                    code = None
                    if body.catalogVersion != catalog["version"]:
                        code = "CatalogChanged"
                    elif offer is None or not offer["available"]:
                        code = "OfferUnavailable"
                    elif offer["purchaseLimit"] and purchased_counts(db, account_id).get(body.offerId, 0) >= offer["purchaseLimit"]:
                        code = "PurchaseLimitReached"
                    elif profile["permanent_currency"] < offer["price"]:
                        code = "InsufficientFunds"

                    if code:
                        receipt = {"operationId": str(body.operationId), "status": "Rejected",
                                   "offerId": body.offerId, "code": code, "replayed": False}
                    else:
                        EnsureInventory(db, account_id)
                        instance_id = uuid4()
                        db.execute("INSERT INTO owned_weapons(instance_id,account_id,configuration_id,grant_key) "
                                   "VALUES (%s,%s,%s,%s)",
                                   (instance_id, account_id, offer["weaponConfigurationId"], "purchase:" + str(body.operationId)))
                        profile = db.execute(
                            "UPDATE profiles SET permanent_currency=permanent_currency-%s,version=version+1,"
                            "wallet_revision=wallet_revision+1,inventory_revision=inventory_revision+1,updated_at=now() "
                            "WHERE account_id=%s RETURNING permanent_currency,wallet_revision,inventory_revision",
                            (offer["price"], account_id)).fetchone()
                        receipt = {"operationId": str(body.operationId), "status": "Completed", "offerId": body.offerId,
                                   "chargedAmount": offer["price"], "currencyId": "prestige",
                                   "walletBalance": profile["permanent_currency"], "walletRevision": profile["wallet_revision"],
                                   "inventoryRevision": profile["inventory_revision"],
                                   "grantedWeaponInstanceIds": [str(instance_id)], "replayed": False}
                    # Business failures must be committed before returning HTTP 409.
                    db.execute("INSERT INTO purchase_operations(account_id,operation_id,offer_id,catalog_version,receipt_json) "
                               "VALUES (%s,%s,%s,%s,%s)",
                               (account_id, body.operationId, body.offerId, body.catalogVersion, Jsonb(receipt)))
        return JSONResponse(status_code=409 if receipt.get("status") == "Rejected" else 200, content=receipt)

    @app.get("/v1/purchases/{operation_id}")
    def purchase_receipt(operation_id: UUID, account_id=Depends(account_from_bearer)):
        with connect() as db:
            row = db.execute("SELECT receipt_json FROM purchase_operations WHERE account_id=%s AND operation_id=%s",
                             (account_id, operation_id)).fetchone()
        if row is None:
            raise HTTPException(404, {"code": "OperationNotFound"})
        return {**row["receipt_json"], "replayed": True}
