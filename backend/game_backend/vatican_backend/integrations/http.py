from uuid import UUID
import httpx

from ..application.errors import ApplicationError
from ..application.ports import Principal


class AuthHttpGateway:
    def __init__(self, client: httpx.Client, base_url: str):
        self.client, self.base_url = client, base_url.rstrip("/")

    def authenticate(self, token):
        try:
            response = self.client.get(self.base_url + "/v1/auth/me", headers={"Authorization": "Bearer " + token})
        except httpx.HTTPError:
            raise ApplicationError(503, "Auth service is unavailable") from None
        if response.status_code in (401, 403):
            raise ApplicationError(401, "Credential is invalid or expired")
        if response.status_code != 200:
            raise ApplicationError(503, "Auth service is unavailable")
        try:
            body = response.json()
            if not isinstance(body, dict) or type(body.get("isGuest")) is not bool:
                raise ValueError()
            return Principal(UUID(body["accountId"]), body["isGuest"])
        except (ValueError, TypeError, KeyError):
            raise ApplicationError(503, "Auth returned an invalid response") from None


class InventoryHttpGateway:
    def __init__(self, client: httpx.Client, base_url: str, secret: str):
        self.client, self.base_url, self.secret = client, base_url.rstrip("/"), secret

    def snapshot(self, account_id):
        try:
            response = self.client.get(self.base_url + f"/internal/players/{account_id}/snapshot",
                                       headers={"X-Service-Secret": self.secret})
            response.raise_for_status()
            value = response.json()
            if not isinstance(value, dict) or not all(isinstance(value.get(key), list) for key in ("Weapons", "Configurations", "Loadout")):
                raise ValueError()
            return value
        except (httpx.HTTPError, ValueError, TypeError):
            raise ApplicationError(503, "Inventory service is unavailable") from None

    def command_status(self, message_id):
        try:
            response = self.client.get(self.base_url + '/internal/broker/commands/' + str(message_id),
                headers={'X-Service-Secret': self.secret})
            if response.status_code == 404:return None
            response.raise_for_status()
            value = response.json()
            if not isinstance(value, dict):raise ValueError()
            return value
        except (httpx.HTTPError, ValueError, TypeError):
            raise ApplicationError(503, 'Inventory command status is unavailable') from None


class HttpRewardDelivery:
    def __init__(self, client: httpx.Client, destinations: dict[str, str], secret: str):
        self.client, self.destinations, self.secret = client, destinations, secret

    def deliver(self, message):
        base_url = self.destinations.get(message["destination"])
        if base_url is None:
            raise ApplicationError(422, "Unknown reward destination")
        try:
            response = self.client.post(base_url.rstrip("/") + "/internal/messages",
                headers={"X-Service-Secret": self.secret}, json={
                    "messageId": str(message["id"]), "eventName": message["event_name"],
                    "schemaVersion": message["schema_version"], "payload": message["payload"],
                })
        except httpx.HTTPError:
            raise ApplicationError(503, "Reward destination is unavailable") from None
        if response.status_code != 200:
            raise ApplicationError(503 if response.status_code >= 500 or response.status_code in (202,401,403,408,429) else 422,
                                   "Reward delivery was not acknowledged")
        try:
            reply = response.json()
            if reply.get("messageId") != str(message["id"]) or reply.get("status") != "APPLIED":
                raise ValueError()
        except (ValueError, AttributeError):
            raise ApplicationError(503, "Invalid reward acknowledgement") from None
        return reply.get("receipt")
