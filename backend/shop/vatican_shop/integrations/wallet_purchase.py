from uuid import UUID

from ..application.errors import ApplicationError, ParticipantConflict
from ..domain.wallet_commands import command, result


class WalletBrokerPurchaseGateway:
    """The committed purchase job is the publication intent; Rabbit confirms never settle it."""
    def __init__(self, publisher, reader, fallback):
        self.publisher, self.reader, self.fallback = publisher, reader, fallback

    def _change(self, job, refund=False):
        envelope = command(job, refund)
        try:
            saved = self.reader.command_status(UUID(envelope['messageId']))
        except ApplicationError:
            self.publisher.publish(envelope)
            raise
        if saved is None:
            self.publisher.publish(envelope)
            raise ApplicationError(503, 'Wallet command is awaiting confirmation')
        try:
            state, receipt = result(job, saved, refund)
        except (ValueError, TypeError, KeyError, AttributeError):
            raise ApplicationError(503, 'Invalid wallet acknowledgement') from None
        if state in ('REJECTED', 'REVIEW'):
            raise ParticipantConflict()
        if state == 'PENDING':
            raise ApplicationError(503, 'Wallet command is awaiting confirmation')
        return receipt

    def charge(self, job):
        return self._change(job)

    def refund(self, job):
        return self._change(job, refund=True)

    def grant(self, job):
        return self.fallback.grant(job)
