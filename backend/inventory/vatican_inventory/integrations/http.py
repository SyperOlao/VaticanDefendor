from uuid import UUID
import httpx

from ..application.errors import ApplicationError
from ..application.models import Principal
from ..domain.weapons import definition_map, STARTER_CONFIGURATION


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
            if (not isinstance(body, dict) or type(body.get("isGuest")) is not bool
                    or not isinstance(body.get("accountId"), str)):
                raise ValueError()
            return Principal(UUID(body["accountId"]), body["isGuest"])
        except (ValueError, TypeError, KeyError):
            raise ApplicationError(503, "Auth returned an invalid response") from None


class DefinitionsHttpGateway:
    def __init__(self, client, base_url):
        self.client, self.base_url = client, base_url.rstrip("/")

    def get(self):
        try:
            response = self.client.get(self.base_url + "/v1/weapon-configurations")
            response.raise_for_status()
            result = response.json()
            if STARTER_CONFIGURATION not in definition_map(result):
                raise ValueError()
            return result
        except (httpx.HTTPError, ValueError, TypeError, KeyError):
            raise ApplicationError(503, "Game content is unavailable") from None
