from dataclasses import dataclass
from enum import StrEnum
import re
from uuid import UUID, uuid5
from .effects import EffectType, GiftEffect

MAX_QUANTITY = 9_223_372_036_854_775_807
MAX_LINES = 1000
MAX_WEAPONS = 100
LEVEL_REWARD_EVENT = 'level.reward'


class RewardType(StrEnum):
    CURRENCY = 'CURRENCY'
    WEAPON = 'WEAPON'
    ITEM = 'ITEM'
    CASE = 'CASE'
    ENERGY = 'ENERGY'
    EFFECT = 'EFFECT'


class DeliveryStatus(StrEnum):
    AVAILABLE = 'AVAILABLE'
    PENDING = 'PENDING'
    PARTIAL = 'PARTIAL'
    COMPLETED = 'COMPLETED'
    REVIEW = 'REVIEW'


class GrantStatus(StrEnum):
    PENDING = 'PENDING'
    COMPLETED = 'COMPLETED'
    FAILED = 'FAILED'


def message_id(claim_id: UUID, item_index: int) -> UUID:
    return uuid5(claim_id, 'level.reward:' + str(item_index))


@dataclass(frozen=True)
class RewardLine:
    item_index: int
    reward_type: str
    reward_ref: str
    quantity: int
    effect: GiftEffect | None = None

    def validate(self):
        if (type(self.item_index) is not int or not 0 <= self.item_index <= 2_147_483_647
                or type(self.quantity) is not int or not 1 <= self.quantity <= MAX_QUANTITY
                or not isinstance(self.reward_type, str) or self.reward_type not in set(RewardType)
                or not isinstance(self.reward_ref, str)):
            raise ValueError('Invalid or unsupported reward line')
        if self.reward_type == RewardType.CURRENCY and self.reward_ref not in ('PRESTIGE', 'GEMS'):
            raise ValueError('Unsupported reward currency')
        if self.reward_type == RewardType.WEAPON and (self.quantity > MAX_WEAPONS
                or re.fullmatch(r'[A-Za-z0-9_.-]{1,80}', self.reward_ref) is None):
            raise ValueError('Invalid weapon reward')
        if self.reward_type == RewardType.ITEM and re.fullmatch(r'[A-Za-z0-9_.:-]{1,128}', self.reward_ref) is None:
            raise ValueError('Invalid item reward')
        if self.reward_type == RewardType.CASE and re.fullmatch(r'[A-Za-z0-9_.-]{1,64}', self.reward_ref) is None:
            raise ValueError('Invalid case reward')
        if self.reward_type == RewardType.ENERGY and self.reward_ref != 'ENERGY':
            raise ValueError('Invalid energy reward')
        if self.reward_type == RewardType.EFFECT:
            if self.reward_ref != EffectType.ENERGY_REGEN_RATE_BONUS or self.quantity != 1 or self.effect is None:
                raise ValueError('Invalid timed effect reward')
            GiftEffect.parse(self.effect.snapshot())
        elif self.effect is not None:
            raise ValueError('Unexpected effect parameters')

    def response(self):
        value = {'itemIndex': self.item_index, 'rewardType': self.reward_type,
                 'rewardRef': self.reward_ref, 'quantity': self.quantity}
        if self.effect is not None:
            value['effect'] = self.effect.snapshot()
        return value

    def payload(self, account_id, claim_id):
        return {'accountId': str(account_id), 'claimId': str(claim_id), **self.response()}


@dataclass(frozen=True)
class RewardPlan:
    bundle_id: UUID
    items: tuple[RewardLine, ...]

    @classmethod
    def parse(cls, value):
        try:
            if (not isinstance(value, dict) or set(value) != {'schemaVersion', 'bundleId', 'items'}
                    or type(value['schemaVersion']) is not int or value['schemaVersion'] != 1
                    or not isinstance(value['items'], list) or not 1 <= len(value['items']) <= MAX_LINES):
                raise ValueError('Invalid or empty reward bundle')
            bundle_id = UUID(value['bundleId'])
            items = []
            indices = set()
            for entry in value['items']:
                fields = {'itemIndex', 'rewardType', 'rewardRef', 'quantity'}
                if not isinstance(entry, dict) or set(entry) not in (fields, fields | {'effect'}):
                    raise ValueError('Invalid reward line fields')
                effect = GiftEffect.parse(entry['effect']) if 'effect' in entry else None
                line = RewardLine(entry['itemIndex'], entry['rewardType'], entry['rewardRef'], entry['quantity'], effect)
                line.validate()
                if line.item_index in indices:
                    raise ValueError('Duplicate reward line')
                indices.add(line.item_index)
                items.append(line)
            if sum(line.quantity for line in items if line.reward_type == RewardType.WEAPON) > MAX_WEAPONS:
                raise ValueError('Too many weapon instances in one reward')
            return cls(bundle_id, tuple(sorted(items, key=lambda line: line.item_index)))
        except (KeyError, TypeError, AttributeError):
            raise ValueError('Invalid reward bundle') from None

    def snapshot(self):
        return {'schemaVersion': 1, 'bundleId': str(self.bundle_id), 'items': [line.response() for line in self.items]}


def validate_receipt(payload: dict, receipt: dict) -> None:
    """An APPLIED response must describe this exact line, not another grant."""
    if (not isinstance(receipt, dict) or any(receipt.get(k) != v for k, v in payload.items())
            or type(receipt.get('itemIndex')) is not int or type(receipt.get('quantity')) is not int):
        raise ValueError('Invalid reward acknowledgement')
    kind = payload['rewardType']
    if kind == RewardType.CURRENCY:
        UUID(receipt['transactionId'])
        if type(receipt.get('balanceAfter')) is not int or not 0 <= receipt['balanceAfter'] <= MAX_QUANTITY:
            raise ValueError('Invalid currency acknowledgement')
    elif kind == RewardType.WEAPON:
        ids = receipt.get('weaponInstanceIds')
        if not isinstance(ids, list) or len(ids) != payload['quantity'] or len(set(ids)) != len(ids):
            raise ValueError('Invalid weapon acknowledgement')
        for value in ids:
            UUID(value)
    elif kind in (RewardType.ITEM, RewardType.CASE):
        expected = ('case:' if kind == RewardType.CASE else 'item:') + payload['rewardRef']
        if (receipt.get('itemId') != expected or type(receipt.get('quantityAfter')) is not int
                or not payload['quantity'] <= receipt['quantityAfter'] <= MAX_QUANTITY):
            raise ValueError('Invalid item acknowledgement')
    else:
        raise ValueError('Unknown remote reward type')
