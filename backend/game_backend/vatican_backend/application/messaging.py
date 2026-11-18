from .ports import UnitOfWorkFactory, RewardDeliveryGateway, PendingDelivery
from .errors import ApplicationError
from ..domain.prestige import PAYMENT_EVENT,validate_receipt as validate_prestige_receipt
from ..domain.analytics import ANALYTICS_EVENTS, validate_analytics_receipt
from ..domain.rating import RATING_EVENT, validate_rating_receipt
from ..domain.level_rewards import LEVEL_REWARD_EVENT, validate_receipt
from ..domain.cases import CASE_REWARD, CASE_CONSUME, CASE_PAYMENT, validate_case_receipt


class DeliverRewards:
    def __init__(self, uow: UnitOfWorkFactory, gateway: RewardDeliveryGateway, cases=None, prestige=None):
        self.uow, self.gateway, self.cases, self.prestige = uow, gateway, cases, prestige

    def once(self):
        with self.uow() as work:
            message = work.messages.claim()
        if message is None:
            return False
        try:
            receipt = self.gateway.deliver(message)
            if isinstance(receipt, PendingDelivery):
                if message['event_name'] != RATING_EVENT or message['destination'] != 'rating':
                    raise ApplicationError(503, 'Unexpected asynchronous delivery')
                with self.uow() as work:
                    work.messages.dispatched(message['id'], message['lease'])
                return True
            if message['event_name'] == PAYMENT_EVENT:
                try:validate_prestige_receipt(message['payload'],receipt)
                except (ValueError,TypeError,KeyError,AttributeError):
                    raise ApplicationError(503,'Prestige payment receipt did not match') from None
            if message['event_name'] in ANALYTICS_EVENTS:
                try:validate_analytics_receipt(message['payload'],receipt,message['event_name'])
                except (ValueError,TypeError,KeyError,AttributeError):
                    raise ApplicationError(503,'Analytics receipt did not match the delivery') from None
            if message['event_name'] == RATING_EVENT:
                try:
                    validate_rating_receipt(message['payload'],receipt)
                except (ValueError,TypeError,KeyError,AttributeError):
                    raise ApplicationError(503,'Rating receipt did not match the delivery') from None
            if message['event_name'] in (CASE_REWARD,CASE_CONSUME,CASE_PAYMENT):
                try:
                    validate_case_receipt(message['event_name'],message['payload'],receipt)
                except (ValueError,TypeError,KeyError,AttributeError):
                    raise ApplicationError(503,'Case receipt did not match the delivery') from None
            if message['event_name'] == LEVEL_REWARD_EVENT:
                try:
                    validate_receipt(message['payload'], receipt)
                except (ValueError, TypeError, KeyError, AttributeError):
                    raise ApplicationError(503, "Reward receipt did not match the delivery") from None
        except ApplicationError as error:
            with self.uow() as work:
                work.messages.retry(message["id"], message["lease"], permanent=error.status_code < 500)
        else:
            try:
                with self.uow() as work:
                    if message['event_name'] in (CASE_CONSUME,CASE_PAYMENT) and self.cases is None:
                        raise ApplicationError(503,'Case settlement is unavailable')
                    if message['event_name']==PAYMENT_EVENT and self.prestige is None:
                        raise ApplicationError(503,'Prestige settlement unavailable')
                    confirmed = work.messages.confirm(message["id"], message["lease"], receipt)
                    if confirmed and message['event_name'] in (CASE_CONSUME,CASE_PAYMENT):
                        self.cases.settle(work,message,receipt)
                    if confirmed and message['event_name']==PAYMENT_EVENT:
                        self.prestige.settle(work,message,receipt)
            except Exception as error:
                # A remote commit may already exist. Roll back the local confirmation,
                # then retain the same source and apply the normal retry/review policy.
                with self.uow() as work:
                    work.messages.retry(message['id'],message['lease'],
                        permanent=isinstance(error,ApplicationError) and error.status_code < 500)
                raise
        return True


    def requeue(self, message_id):
        with self.uow() as work:
            return work.messages.requeue(message_id)
