from .ports import UnitOfWorkFactory, DefinitionsGateway, PurchaseGateway
from .errors import ApplicationError, ParticipantConflict
from .catalog import DELIVERY_UNCONFIRMED, PurchaseState, ReceiptStatus
from ..domain.purchases import validate_request


def public_receipt(receipt):
    return {k: v for k, v in receipt.items() if k != "plan" and not k.startswith("_")}


class ShopUseCases:
    def __init__(self, uow: UnitOfWorkFactory, catalog: DefinitionsGateway):
        self.uow, self.catalog = uow, catalog

    def get_catalog(self, account_id):
        catalog = self.catalog.get()
        with self.uow() as work:
            counts = work.repository.counts(account_id)
        offers = []
        for offer in catalog["offers"]:
            count = counts.get(offer["id"], 0)
            limited = offer["purchaseLimit"] and count >= offer["purchaseLimit"]
            offers.append({**offer, "purchasedCount": count, "available": offer["available"] and not limited,
                           "unavailableReason": "PurchaseLimitReached" if limited else offer["unavailableReason"]})
        return {**catalog, "offers": offers}

    def purchase(self, account_id, operation_id, offer_id, catalog_version):
        try:
            validate_request(account_id, operation_id, offer_id, catalog_version)
        except ValueError:
            raise ApplicationError(422, "Invalid purchase source") from None
        # Replays remain available even if the current catalog service is down.
        with self.uow() as work:
            prior = work.repository.operation(account_id, operation_id)
            if prior:
                return self._replay(prior, offer_id, catalog_version)
        catalog = self.catalog.get()
        with self.uow() as work:
            repo = work.repository
            repo.lock_player(account_id)
            prior = repo.operation(account_id, operation_id)
            if prior:
                return self._replay(prior, offer_id, catalog_version)
            offer = next((o for o in catalog["offers"] if o["id"] == offer_id), None)
            code = None
            current_revision = work.catalog.revision() if "revision" in catalog else None
            if catalog["version"] != catalog_version or current_revision != catalog.get("revision"):
                code = "CatalogChanged"
            elif not offer or not offer["available"]:
                code = "OfferUnavailable"
            elif offer["purchaseLimit"] and repo.counts(account_id).get(offer_id, 0) >= offer["purchaseLimit"]:
                code = "PurchaseLimitReached"
            receipt = {"operationId": str(operation_id), "offerId": offer_id,
                       "status": ReceiptStatus.REJECTED if code else ReceiptStatus.PENDING, "replayed": False}
            if code:
                receipt["code"] = code
            repo.create(account_id, operation_id, offer_id, catalog_version, receipt,
                        {"price": offer["price"], "configurationId": offer["weaponConfigurationId"]} if not code else {})
            return public_receipt(repo.operation(account_id,operation_id).receipt_json)

    def _replay(self, prior, offer_id, catalog_version):
        if prior.offer_id != offer_id or prior.catalog_version != catalog_version:
            raise ApplicationError(409, "Purchase operation payload changed")
        return {**public_receipt(prior.receipt_json), "replayed": True}

    def receipt(self, account_id, operation_id):
        with self.uow() as work:
            prior = work.repository.operation(account_id, operation_id)
            if prior is None:
                raise ApplicationError(404, "Purchase operation not found")
            return public_receipt(prior.receipt_json)

    def history(self, account_id, limit=20, before=None):
        if type(limit) is not int or not 1 <= limit <= 100:
            raise ApplicationError(422, "History limit must be between 1 and 100")
        with self.uow() as work:
            rows = work.repository.history(account_id, limit + 1, before)
        return {"purchases": [{**public_receipt(row.receipt_json), "createdAt": row.created_at.isoformat()}
                              for row in rows[:limit]],
                "nextCursor": str(rows[limit - 1].operation_id) if len(rows) > limit else None}


class SettlePurchases:
    def __init__(self, uow: UnitOfWorkFactory, gateway: PurchaseGateway):
        self.uow, self.gateway = uow, gateway

    def once(self):
        with self.uow() as work:
            job = work.repository.claim()
        if job is None:
            return False
        if job["state"] == PurchaseState.REVIEW:
            return True
        state, receipt, retry = job["state"], None, False
        try:
            if state == PurchaseState.PENDING:
                if job["plan"]["price"]:
                    result = self.gateway.charge(job)
                else:
                    result = {"status": "APPLIED"}
                if result["status"] == "REJECTED":
                    state, receipt = PurchaseState.REJECTED, {"status": ReceiptStatus.REJECTED,
                                                              "code": "InsufficientFunds"}
                else:
                    state = PurchaseState.CHARGED
            elif state == PurchaseState.CHARGED:
                result = self.gateway.grant(job)
                if result["status"] == "REJECTED":
                    state = PurchaseState.REFUND_PENDING
                else:
                    state = PurchaseState.COMPLETED
                    receipt = {"status": ReceiptStatus.COMPLETED, "chargedAmount": job["plan"]["price"],
                               "currencyId": "PRESTIGE", "grantedWeaponInstanceIds": [result["instanceId"]]}
                    if "inventoryRevision" in result:
                        receipt["inventoryRevision"] = result["inventoryRevision"]
            elif state == PurchaseState.REFUND_PENDING:
                if job["plan"]["price"]:
                    result = self.gateway.refund(job)
                    if result["status"] != "APPLIED":
                        raise ParticipantConflict()
                state, receipt = PurchaseState.REJECTED, {"status": ReceiptStatus.REJECTED, "code": "GrantRejected"}
        except ParticipantConflict:
            receipt = {"status": ReceiptStatus.REVIEW, "code": "ParticipantConflict", "_resumeState": state}
            state = PurchaseState.REVIEW
        except ApplicationError as error:
            if error.status_code in (409, 422):
                receipt = {"status": ReceiptStatus.REVIEW, "code": "ParticipantConflict", "_resumeState": state}
                state = PurchaseState.REVIEW
            else:
                retry = True
        with self.uow() as work:
            work.repository.transition(job, state, receipt, retry, DELIVERY_UNCONFIRMED if retry else None)
        return True


    def requeue(self, account_id, operation_id):
        with self.uow() as work:
            return work.repository.requeue(account_id, operation_id)
