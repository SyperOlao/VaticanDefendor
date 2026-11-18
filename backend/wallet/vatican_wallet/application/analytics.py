from .ports import UnitOfWorkFactory, AnalyticsGateway
from .errors import ApplicationError
from ..domain.analytics import validate_receipt


class DeliverAnalytics:
    def __init__(self, uow: UnitOfWorkFactory, gateway: AnalyticsGateway):
        self.uow, self.gateway = uow, gateway

    def once(self):
        with self.uow() as work:
            message = work.repository.claim_analytics()
        if message is None:
            return False
        if message.get("exhausted"):
            return True
        # HTTP and ClickHouse projection happen without an open wallet transaction.
        try:
            receipt = self.gateway.deliver(message)
            try:
                validate_receipt(message["payload"], receipt)
            except (ValueError, TypeError, KeyError, AttributeError):
                raise ApplicationError(503, "Analytics receipt did not match the transaction") from None
        except ApplicationError as error:
            with self.uow() as work:
                work.repository.retry_analytics(message["id"], message["lease"], error.status_code < 500)
        else:
            with self.uow() as work:
                work.repository.confirm_analytics(message["id"], message["lease"], receipt)
        return True

    def requeue(self, transaction_id):
        with self.uow() as work:
            return work.repository.requeue_analytics(transaction_id)
