from uuid import uuid4

from .errors import ApplicationError


class ShopUseCases:
    def __init__(self, uow, catalog, weapons):
        self.uow = uow
        self.catalog = catalog
        self.offers = {offer["id"]: offer for offer in catalog["offers"]}
        self.weapons = weapons

    def get_catalog(self, account_id):
        with self.uow() as work:
            work.profiles.get(account_id, lock=True)
            profile = work.profiles.get(account_id)
            counts = work.shop.purchased_counts(account_id)
            offers = []
            for offer in self.catalog["offers"]:
                count = counts.get(offer["id"], 0)
                reason = ""
                if not offer["available"]:
                    reason = offer["unavailableReason"] or "OfferUnavailable"
                elif offer["purchaseLimit"] and count >= offer["purchaseLimit"]:
                    reason = "PurchaseLimitReached"
                offers.append({**offer, "purchasedCount": count, "available": not reason, "unavailableReason": reason})
            return {**self.catalog, "offers": offers, "walletBalance": profile.permanent_currency,
                    "walletRevision": profile.wallet_revision}

    def inventory(self, account_id):
        with self.uow() as work:
            return {"items": [{"itemId": row.item_id, "quantity": row.quantity}
                              for row in work.weapons.legacy_inventory(account_id)]}

    def purchase(self, account_id, operation_id, offer_id, catalog_version):
        with self.uow() as work:
            work.profiles.get(account_id, lock=True)
            prior = work.shop.operation(account_id, operation_id)
            if prior:
                if prior.offer_id != offer_id or prior.catalog_version != catalog_version:
                    raise ApplicationError(409, {"code": "OperationConflict", "operationId": str(operation_id)})
                return {**prior.receipt_json, "replayed": True}
            profile = work.profiles.get(account_id, lock=True)
            offer = self.offers.get(offer_id)
            code = None
            if catalog_version != self.catalog["version"]:
                code = "CatalogChanged"
            elif offer is None or not offer["available"]:
                code = "OfferUnavailable"
            elif offer["purchaseLimit"] and work.shop.purchased_counts(account_id).get(offer_id, 0) >= offer["purchaseLimit"]:
                code = "PurchaseLimitReached"
            elif profile.permanent_currency < offer["price"]:
                code = "InsufficientFunds"
            if code:
                receipt = {"operationId": str(operation_id), "status": "Rejected", "offerId": offer_id,
                           "code": code, "replayed": False}
            else:
                self.weapons.ensure_inventory(work, account_id)
                instance_id = uuid4()
                work.weapons.add(instance_id=instance_id, account_id=account_id,
                                  configuration_id=offer["weaponConfigurationId"], grant_key="purchase:" + str(operation_id))
                profile = work.profiles.change_currency(account_id, -offer["price"], inventory_delta=1)
                receipt = {"operationId": str(operation_id), "status": "Completed", "offerId": offer_id,
                           "chargedAmount": offer["price"], "currencyId": "prestige",
                           "walletBalance": profile.permanent_currency, "walletRevision": profile.wallet_revision,
                           "inventoryRevision": profile.inventory_revision,
                           "grantedWeaponInstanceIds": [str(instance_id)], "replayed": False}
            work.shop.record(account_id=account_id, operation_id=operation_id, offer_id=offer_id,
                              catalog_version=catalog_version, receipt_json=receipt)
            # Rejections are results, not exceptions: persist before the adapter sends 409.
            return receipt

    def receipt(self, account_id, operation_id):
        with self.uow() as work:
            row = work.shop.operation(account_id, operation_id)
            if row is None:
                raise ApplicationError(404, {"code": "OperationNotFound"})
            return {**row.receipt_json, "replayed": True}
