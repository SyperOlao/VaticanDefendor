import httpx
from ..application.errors import ApplicationError


class HttpAnalyticsGateway:
    def __init__(self, client, url, secret):
        self.client, self.url, self.secret = client, url.rstrip("/"), secret

    def deliver(self, message):
        try:
            response = self.client.post(self.url + "/internal/messages",
                headers={"X-Service-Secret": self.secret}, json={"messageId": str(message["id"]),
                    "eventName": message["event_name"], "schemaVersion": message["schema_version"],
                    "payload": message["payload"]})
        except httpx.HTTPError:
            raise ApplicationError(503, "Analytics is unavailable") from None
        if response.status_code != 200:
            # 202 means a durable inbox, not completed warehouse projection.
            transient = response.status_code >= 500 or response.status_code in (202, 401, 403, 408, 429)
            raise ApplicationError(503 if transient else 422, "Wallet analytics delivery was not acknowledged")
        try:
            result = response.json()
            if result["messageId"] != str(message["id"]) or result["status"] != "APPLIED":
                raise ValueError()
            return result["receipt"]
        except (ValueError, TypeError, KeyError, AttributeError):
            raise ApplicationError(503, "Invalid analytics acknowledgement") from None
