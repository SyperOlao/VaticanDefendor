import hashlib
import json
from uuid import UUID, uuid5
from .catalog import CurrencyCode, EventName, RewardType, MessageStatus
from .errors import ApplicationError
from .ports import UnitOfWorkFactory


class LevelRewardConsumer:
    def __init__(self, uow: UnitOfWorkFactory):
        self.uow = uow

    def receive(self, message_id, event_name, schema_version, payload):
        case = event_name == EventName.CASE_REWARD
        source_field = 'openingId' if case else 'claimId'
        try:
            if (event_name not in (EventName.LEVEL_REWARD,EventName.CASE_REWARD) or type(schema_version) is not int or schema_version != 1
                    or not isinstance(payload, dict)
                    or set(payload) != {'accountId', source_field, 'itemIndex', 'rewardType', 'rewardRef', 'quantity'}):
                raise ValueError()
            account, claim = UUID(payload['accountId']), UUID(payload[source_field])
            index, quantity, currency = payload['itemIndex'], payload['quantity'], payload['rewardRef']
            if (payload['rewardType'] != RewardType.CURRENCY or currency not in set(CurrencyCode)
                    or type(index) is not int or not 0 <= index <= 2_147_483_647
                    or type(quantity) is not int or not 1 <= quantity <= 9_223_372_036_854_775_807):
                raise ValueError()
            digest = hashlib.sha256(json.dumps([event_name, schema_version, payload], sort_keys=True, separators=(',', ':')).encode()).digest()
        except (ValueError, TypeError, KeyError, AttributeError):
            raise ApplicationError(422, 'Invalid level currency reward') from None
        with self.uow() as work:
            repo = work.repository
            repo.lock_message(message_id)
            repo.lock_player(uuid5(claim, ('case.reward:' if case else 'level.reward:') + str(index)))
            repo.lock_player(account)
            consumed = repo.received(message_id, digest)
            receipt = (repo.case_receipt if case else repo.level_receipt)(claim,index,account,digest)
            if receipt is None:
                if consumed:
                    raise ApplicationError(409, 'Reward message has no source receipt')
                result = repo.award_level_currency(account,claim,index,currency,quantity,case=case)
                receipt = {**payload, **result}
                (repo.record_case_receipt if case else repo.record_level_receipt)(claim,index,account,digest,receipt)
            if not consumed:
                repo.record_message(message_id, digest)
            return {'messageId': str(message_id), 'status': MessageStatus.APPLIED, 'receipt': receipt}
