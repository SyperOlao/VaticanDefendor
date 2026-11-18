from ..application.errors import ApplicationError
from ..domain.inventory_commands import command, result


class InventoryRabbitDelivery:
    def __init__(self, publisher, reader, fallback):
        self.publisher, self.reader, self.fallback = publisher, reader, fallback

    def deliver(self, message):
        if message['destination'] != 'inventory':return self.fallback.deliver(message)
        try:
            envelope = command(message)
        except (ValueError, TypeError, KeyError):
            raise ApplicationError(422, 'Invalid inventory delivery') from None
        saved = self.reader.command_status(message['id'])
        if saved is None:
            self.publisher.publish(envelope)
            raise ApplicationError(503, 'Inventory command is awaiting confirmation')
        try:
            status, receipt = result(message, saved)
        except (ValueError, TypeError, KeyError, AttributeError):
            raise ApplicationError(503, 'Invalid inventory acknowledgement') from None
        if status in ('REJECTED', 'REVIEW'):
            raise ApplicationError(409, 'Inventory command requires reconciliation')
        if status == 'PENDING':
            raise ApplicationError(503, 'Inventory command is awaiting confirmation')
        return receipt
