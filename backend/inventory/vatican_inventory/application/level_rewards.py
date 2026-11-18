import hashlib
import json
import re
from uuid import UUID, uuid5
from .catalog import EventName, RewardType, MessageStatus
from .errors import ApplicationError
from .ports import UnitOfWorkFactory, DefinitionsGateway
from ..domain.items import add_quantity
from ..domain.weapons import definition_map


class LevelRewardConsumer:
    def __init__(self, uow: UnitOfWorkFactory, definitions: DefinitionsGateway):
        self.uow, self.definitions = uow, definitions

    @staticmethod
    def _award(repo, account, claim, index, kind, ref, quantity, case):
        repo.ensure(account)
        if kind == RewardType.WEAPON:
            prefix = "case" if case else "level"
            identities = [str(repo.grant(account, f"{prefix}:{claim.hex}:{index}:{unit}", ref)[0])
                          for unit in range(quantity)]
            return {"weaponInstanceIds": identities, "inventoryRevision": repo.state(account).revision}
        item_id = ("case:" if kind == RewardType.CASE else "item:") + ref
        try:
            balance = add_quantity(repo.quantity(account, item_id), quantity)
        except ValueError:
            raise ApplicationError(409, "Item capacity exceeded") from None
        repo.set_quantity(account, item_id, balance)
        return {"itemId": item_id, "quantityAfter": balance, "inventoryRevision": repo.state(account).revision}

    @staticmethod
    def _prior(repo, message_id, claim, index, account, digest, case=False):
        repo.lock_message(message_id)
        repo.lock_player(uuid5(claim, ('case.reward:' if case else 'level.reward:') + str(index)))
        repo.lock_player(account)
        consumed = repo.received(message_id, digest)
        receipt = (repo.case_receipt if case else repo.level_receipt)(claim,index,account,digest)
        if receipt is None and consumed:
            raise ApplicationError(409, 'Reward message has no source receipt')
        if receipt is not None and not consumed:
            repo.record_message(message_id, digest)
        return receipt

    def receive(self, message_id, event_name, schema_version, payload):
        case = event_name == EventName.CASE_REWARD
        source_field = 'openingId' if case else 'claimId'
        try:
            if (event_name not in (EventName.LEVEL_REWARD,EventName.CASE_REWARD) or type(schema_version) is not int or schema_version != 1
                    or not isinstance(payload, dict)
                    or set(payload) != {'accountId', source_field, 'itemIndex', 'rewardType', 'rewardRef', 'quantity'}):
                raise ValueError()
            account, claim = UUID(payload['accountId']), UUID(payload[source_field])
            index, quantity, kind, ref = payload['itemIndex'], payload['quantity'], payload['rewardType'], payload['rewardRef']
            if (type(index) is not int or not 0 <= index <= 2_147_483_647
                    or type(quantity) is not int or not 1 <= quantity <= 9_223_372_036_854_775_807
                    or not isinstance(kind, str) or kind not in set(RewardType) or not isinstance(ref, str)):
                raise ValueError()
            pattern = r'[A-Za-z0-9_.-]{1,80}' if kind == RewardType.WEAPON else (
                r'[A-Za-z0-9_.-]{1,64}' if kind == RewardType.CASE else r'[A-Za-z0-9_.:-]{1,128}')
            if re.fullmatch(pattern, ref) is None or kind == RewardType.WEAPON and quantity > 100:
                raise ValueError()
            digest = hashlib.sha256(json.dumps([event_name, schema_version, payload], sort_keys=True, separators=(',', ':')).encode()).digest()
        except (ValueError, TypeError, KeyError, AttributeError):
            raise ApplicationError(422, 'Invalid level inventory reward') from None
        # Replays do not need a live content service, including after a lost acknowledgement.
        with self.uow() as work:
            receipt = self._prior(work.repository,message_id,claim,index,account,digest,case)
            if receipt is not None:
                return {'messageId': str(message_id), 'status': MessageStatus.APPLIED, 'receipt': receipt}
        if kind == RewardType.WEAPON:
            try:
                definitions = definition_map(self.definitions.get())
            except (ValueError, TypeError, KeyError):
                raise ApplicationError(503, 'Game content is invalid') from None
            if ref not in definitions:
                raise ApplicationError(422, 'Unknown weapon reward definition')
        with self.uow() as work:
            repo = work.repository
            receipt = self._prior(repo,message_id,claim,index,account,digest,case)
            if receipt is None:
                result = self._award(repo,account,claim,index,kind,ref,quantity,case)
                receipt = {**payload, **result}
                (repo.record_case_receipt if case else repo.record_level_receipt)(claim,index,account,digest,receipt)
                repo.record_message(message_id, digest)
            return {'messageId': str(message_id), 'status': MessageStatus.APPLIED, 'receipt': receipt}
