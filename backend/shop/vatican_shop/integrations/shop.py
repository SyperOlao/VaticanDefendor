import httpx
from ..application.errors import ApplicationError, ParticipantConflict
from ..domain.inventory_commands import grant_payload, validate_grant_receipt


class PurchaseGateway:
    def __init__(self, client, settings):
        self.client, self.settings = client, settings

    def _post(self, url, body, rejection_code):
        try:
            response = self.client.post(url, json=body, headers={"X-Service-Secret": self.settings.service_secret})
        except httpx.HTTPError:
            raise ApplicationError(503, "Purchase participant is unavailable") from None
        if response.status_code in (409, 422):
            # Generic errors cannot prove that a charge/grant did not happen earlier.
            raise ParticipantConflict()
        if response.status_code != 200:
            raise ApplicationError(503, "Purchase participant is unavailable")
        try:
            result = response.json()
            if not isinstance(result, dict) or result.get("status") not in ("APPLIED", "REJECTED"):
                raise ValueError()
            if any(result.get(key) != value or type(result.get(key)) is not type(value) for key, value in body.items()):
                raise ValueError()
            if "amount" in body and result.get("currencyId") != "PRESTIGE":
                raise ValueError()
            if result["status"] == "REJECTED" and result.get("code") != rejection_code:
                raise ValueError()
            return result
        except (ValueError, AttributeError):
            raise ApplicationError(503, "Invalid purchase acknowledgement") from None

    def charge(self, job):
        return self._post(self.settings.wallet_url.rstrip("/") + "/internal/charges", {
            "accountId": str(job["accountId"]), "operationId": str(job["operationId"]), "amount": job["plan"]["price"]},
            "InsufficientFunds")

    def refund(self, job):
        return self._post(self.settings.wallet_url.rstrip("/") + "/internal/refunds", {
            "accountId": str(job["accountId"]), "operationId": str(job["operationId"]), "amount": job["plan"]["price"]},
            "ChargeNotFound")

    def grant(self, job):
        result = self._post(self.settings.inventory_url.rstrip("/") + "/internal/grants", grant_payload(job),
                            "UnknownWeaponConfiguration")
        try:
            return validate_grant_receipt(job, result)
        except (ValueError, TypeError, AttributeError):
            raise ApplicationError(503, "Invalid inventory acknowledgement") from None
