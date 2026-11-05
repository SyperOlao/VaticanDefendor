"""Server configured offers. A purchase is one wallet/inventory/receipt transaction."""
import re
from uuid import UUID

from fastapi import Depends, HTTPException
from pydantic import BaseModel, ConfigDict, Field
from psycopg.types.json import Jsonb


class Purchase(BaseModel):
    model_config = ConfigDict(extra="forbid")
    operationId: UUID
    offerId: str = Field(pattern=r"^[a-z0-9_.-]{1,64}$")
    catalogVersion: str = Field(min_length=1, max_length=64)


def validate_catalog(catalog):
    if catalog == {}:
        return {"version": "empty", "offers": []}
    if not isinstance(catalog, dict) or set(catalog) != {"version", "offers"}:
        raise ValueError("Shop catalog requires version and offers")
    if not isinstance(catalog["version"], str) or not re.fullmatch(r"[A-Za-z0-9_.-]{1,64}", catalog["version"]):
        raise ValueError("Invalid catalog version")
    if not isinstance(catalog["offers"], list) or len(catalog["offers"]) > 200:
        raise ValueError("Catalog permits at most 200 offers")
    ids = set()
    for offer in catalog["offers"]:
        if not isinstance(offer, dict) or set(offer) != {"id", "itemId", "title", "price", "quantity", "maxOwned"}:
            raise ValueError("Invalid offer fields")
        for key in ("id", "itemId"):
            if not isinstance(offer[key], str) or not re.fullmatch(r"[a-z0-9_.-]{1,64}", offer[key]):
                raise ValueError("Invalid offer/item identifier")
        if offer["id"] in ids:
            raise ValueError("Duplicate offer ID")
        ids.add(offer["id"])
        if not isinstance(offer["title"], str) or not 1 <= len(offer["title"].strip()) <= 80:
            raise ValueError("Invalid offer title")
        for key in ("price", "quantity", "maxOwned"):
            if type(offer[key]) is not int or not 1 <= offer[key] <= 1_000_000_000:
                raise ValueError("Offer price, quantity, maxOwned must be positive bounded integers")
        if offer["quantity"] > offer["maxOwned"]:
            raise ValueError("Offer quantity exceeds ownership limit")
    return catalog


def install_shop(app, connect, account_from_bearer, configured):
    catalog = validate_catalog(configured)
    offers = {offer["id"]: offer for offer in catalog["offers"]}

    @app.get("/v1/shop")
    def get_catalog(account_id=Depends(account_from_bearer)):
        return catalog

    @app.get("/v1/inventory")
    def get_inventory(account_id=Depends(account_from_bearer)):
        with connect() as db:
            rows = db.execute("SELECT item_id,quantity FROM inventory WHERE account_id=%s ORDER BY item_id",
                              (account_id,)).fetchall()
        return {"items": [{"itemId": row["item_id"], "quantity": row["quantity"]} for row in rows]}

    @app.post("/v1/purchases")
    def purchase(body: Purchase, account_id=Depends(account_from_bearer)):
        with connect() as db:
            with db.transaction():
                profile = db.execute("SELECT permanent_currency,version FROM profiles WHERE account_id=%s FOR UPDATE",
                                     (account_id,)).fetchone()
                prior = db.execute("SELECT * FROM purchase_operations WHERE account_id=%s AND operation_id=%s",
                                   (account_id, body.operationId)).fetchone()
                if prior:
                    if prior["offer_id"] != body.offerId or prior["catalog_version"] != body.catalogVersion:
                        raise HTTPException(409, "Operation ID already used for another purchase")
                    return {**prior["receipt_json"], "replayed": True}
                if body.catalogVersion != catalog["version"]:
                    raise HTTPException(409, "Catalog changed; reload offers")
                offer = offers.get(body.offerId)
                if offer is None:
                    raise HTTPException(404, "Offer not found")
                if profile["permanent_currency"] < offer["price"]:
                    raise HTTPException(409, "Insufficient permanent currency")
                owned = db.execute("SELECT quantity FROM inventory WHERE account_id=%s AND item_id=%s",
                                   (account_id, offer["itemId"])).fetchone()
                quantity = (owned["quantity"] if owned else 0) + offer["quantity"]
                if quantity > offer["maxOwned"]:
                    raise HTTPException(409, "Ownership limit reached")
                profile = db.execute("UPDATE profiles SET permanent_currency=permanent_currency-%s, "
                                     "version=version+1,updated_at=now() WHERE account_id=%s "
                                     "RETURNING permanent_currency,version", (offer["price"], account_id)).fetchone()
                db.execute("INSERT INTO inventory(account_id,item_id,quantity) VALUES (%s,%s,%s) "
                           "ON CONFLICT(account_id,item_id) DO UPDATE SET quantity=EXCLUDED.quantity",
                           (account_id, offer["itemId"], quantity))
                receipt = {"operationId": str(body.operationId), "offerId": body.offerId,
                           "catalogVersion": body.catalogVersion, "itemId": offer["itemId"],
                           "quantity": quantity, "charged": offer["price"],
                           "permanentCurrency": profile["permanent_currency"], "profileVersion": profile["version"]}
                db.execute("INSERT INTO purchase_operations(account_id,operation_id,offer_id,catalog_version,receipt_json) "
                           "VALUES (%s,%s,%s,%s,%s)",
                           (account_id, body.operationId, body.offerId, body.catalogVersion, Jsonb(receipt)))
        return {**receipt, "replayed": False}

    @app.get("/v1/purchases/{operation_id}")
    def purchase_receipt(operation_id: UUID, account_id=Depends(account_from_bearer)):
        with connect() as db:
            row = db.execute("SELECT receipt_json FROM purchase_operations WHERE account_id=%s AND operation_id=%s",
                             (account_id, operation_id)).fetchone()
        if row is None:
            raise HTTPException(404, "Purchase not found")
        return {**row["receipt_json"], "replayed": True}
