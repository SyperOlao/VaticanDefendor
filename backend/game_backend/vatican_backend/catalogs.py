"""Pure validation and versioning of deployment shop catalogs."""
import hashlib
import json
import re
from copy import deepcopy


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


