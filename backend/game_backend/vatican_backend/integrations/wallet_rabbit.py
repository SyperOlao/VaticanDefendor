from ..application.errors import ApplicationError
from ..domain.wallet_commands import command, result


class WalletRabbitDelivery:
    def __init__(self, publisher, reader, fallback):
        self.publisher, self.reader, self.fallback = publisher, reader, fallback

    def deliver(self, message):
        if message['destination'] != 'wallet':return self.fallback.deliver(message)
        try:
            envelope = command(message)
        except (ValueError, TypeError, KeyError):
            raise ApplicationError(422, 'Invalid wallet delivery') from None
        try:
            saved = self.reader.command_status(message['id'])
        except ApplicationError:
            # The receiver may be down; publish to its durable queue anyway.
            self.publisher.publish(envelope)
            raise
        if saved is None:
            self.publisher.publish(envelope)
            raise ApplicationError(503, 'Wallet command is awaiting confirmation')
        try:
            status, receipt = result(message, saved)
        except (ValueError, TypeError, KeyError, AttributeError):
            raise ApplicationError(503, 'Invalid wallet acknowledgement') from None
        if status in ('REJECTED', 'REVIEW'):
            raise ApplicationError(409, 'Wallet command requires reconciliation')
        if status == 'PENDING':
            raise ApplicationError(503, 'Wallet command is awaiting confirmation')
        return receipt
