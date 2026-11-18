import httpx

from .shop import PurchaseGateway
from ..application.errors import ApplicationError, ParticipantConflict
from ..application.ports import InventoryCommandPublisher
from ..domain.inventory_commands import InventoryCommandStatus, broker_result, grant_command


class BrokerPurchaseGateway(PurchaseGateway):
    def __init__(self, client, settings, publisher: InventoryCommandPublisher, reader=None):
        super().__init__(client, settings)
        self.publisher = publisher
        self.reader = reader

    def _status(self, identity):
        if self.reader is not None:
            value = self.reader.command_status(identity)
            return 404 if value is None else 200, value
        try:
            response = self.client.get(self.settings.inventory_url.rstrip('/') + '/internal/broker/commands/' + identity,
                headers={'X-Service-Secret': self.settings.service_secret})
        except httpx.HTTPError:
            raise ApplicationError(503, 'Inventory command status is unavailable') from None
        if response.status_code != 200:return response.status_code, None
        try:return 200, response.json()
        except (ValueError, TypeError):
            raise ApplicationError(503, 'Invalid inventory command acknowledgement') from None

    def grant(self, job):
        command = grant_command(job)
        status_code, body = self._status(command['messageId'])
        if status_code == 404:
            # The committed CHARGED job is the durable publication intent. A lost confirm
            # repeats this exact command ID and business source after the job lease/backoff.
            try:
                self.publisher.publish(command)
            except Exception:
                raise ApplicationError(503, "Inventory command publication is unavailable") from None
            raise ApplicationError(503, "Inventory command is awaiting confirmation")
        if status_code != 200:
            raise ApplicationError(503, "Inventory command status is unavailable")
        if self.reader is not None and (not isinstance(body, dict) or 'payloadSha256' not in body):
            raise ApplicationError(503, 'Invalid inventory command acknowledgement')
        try:
            status, receipt = broker_result(job, body)
        except (ValueError, TypeError, KeyError, AttributeError, RecursionError):
            raise ApplicationError(503, "Invalid inventory command acknowledgement") from None
        if status in (InventoryCommandStatus.REJECTED, InventoryCommandStatus.REVIEW):
            raise ParticipantConflict()
        if status == InventoryCommandStatus.PENDING:
            raise ApplicationError(503, "Inventory command is awaiting confirmation")
        return receipt
