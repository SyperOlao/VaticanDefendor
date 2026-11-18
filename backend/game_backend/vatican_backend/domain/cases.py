"""Validated case rules, weighted draws and durable protocol acknowledgements."""
from dataclasses import dataclass
from enum import StrEnum
import re
import hashlib
import hmac
from uuid import UUID, uuid5
from .level_rewards import RewardPlan, MAX_QUANTITY, validate_receipt

CASE_REWARD = 'case.reward'
CASE_CONSUME = 'case.consume'
CASE_PAYMENT = 'case.payment'


class OpeningStatus(StrEnum):
    FUNDING = 'FUNDING'
    PENDING = 'PENDING'
    REVIEW = 'REVIEW'
    COMPLETED = 'COMPLETED'
    REJECTED = 'REJECTED'


class PaymentMode(StrEnum):
    INVENTORY = 'INVENTORY'
    WALLET = 'WALLET'


@dataclass(frozen=True)
class Drop:
    index: int
    rarity: str
    weight: int
    plan: RewardPlan


@dataclass(frozen=True)
class CaseRules:
    id: UUID
    code: str
    ruleset_id: str
    currency: str
    price: int
    pity_threshold: int
    guaranteed_rarity: str
    drops: tuple[Drop, ...]

    @classmethod
    def parse(cls, value):
        try:
            if not isinstance(value, dict) or set(value) != {'schemaVersion','caseId','code','rulesetId',
                    'priceCurrency','price','pityThreshold','guaranteedRarity','drops'}:
                raise ValueError()
            if type(value['schemaVersion']) is not int or value['schemaVersion'] != 1:
                raise ValueError()
            identity = UUID(value['caseId'])
            code, ruleset = value['code'], value['rulesetId']
            if (not isinstance(code,str) or re.fullmatch(r'[A-Za-z0-9_.-]{1,64}',code) is None
                    or not isinstance(ruleset,str) or re.fullmatch(r'[A-Za-z0-9_.-]{1,128}',ruleset) is None
                    or value['priceCurrency'] not in ('PRESTIGE','GEMS')
                    or type(value['price']) is not int or not 0 <= value['price'] <= MAX_QUANTITY
                    or type(value['pityThreshold']) is not int or not 1 <= value['pityThreshold'] <= 1_000_000
                    or not isinstance(value['guaranteedRarity'],str)
                    or re.fullmatch(r'[A-Za-z0-9_.-]{1,32}',value['guaranteedRarity']) is None
                    or not isinstance(value['drops'],list) or not 1 <= len(value['drops']) <= 1000):
                raise ValueError()
            drops = []
            for entry in value['drops']:
                if not isinstance(entry,dict) or set(entry) != {'entryIndex','rarity','weight','reward'}:
                    raise ValueError()
                if (type(entry['entryIndex']) is not int or not 0 <= entry['entryIndex'] <= 2_147_483_647
                        or not isinstance(entry['rarity'],str) or re.fullmatch(r'[A-Za-z0-9_.-]{1,32}',entry['rarity']) is None
                        or type(entry['weight']) is not int or not 1 <= entry['weight'] <= MAX_QUANTITY):
                    raise ValueError()
                drops.append(Drop(entry['entryIndex'],entry['rarity'],entry['weight'],RewardPlan.parse(entry['reward'])))
            if (len({d.index for d in drops}) != len(drops) or sum(d.weight for d in drops) > MAX_QUANTITY
                    or sum(len(d.plan.items) for d in drops) > 5000
                    or not any(d.rarity == value['guaranteedRarity'] for d in drops)):
                raise ValueError()
            return cls(identity,code,ruleset,value['priceCurrency'],value['price'],value['pityThreshold'],
                       value['guaranteedRarity'],tuple(sorted(drops,key=lambda d:d.index)))
        except (KeyError,TypeError,AttributeError,ValueError):
            raise ValueError('Invalid case configuration') from None

    def snapshot(self):
        return {'schemaVersion':1,'caseId':str(self.id),'code':self.code,'rulesetId':self.ruleset_id,
                'priceCurrency':self.currency,'price':self.price,'pityThreshold':self.pity_threshold,
                'guaranteedRarity':self.guaranteed_rarity,'drops':[
                    {'entryIndex':d.index,'rarity':d.rarity,'weight':d.weight,'reward':d.plan.snapshot()} for d in self.drops]}

    def draw(self, pity_before, randbelow):
        if type(pity_before) is not int or not 0 <= pity_before <= 2_147_483_647:
            raise ValueError('Invalid pity counter')
        guaranteed = pity_before + 1 >= self.pity_threshold
        pool = [d for d in self.drops if not guaranteed or d.rarity == self.guaranteed_rarity]
        total = sum(d.weight for d in pool)
        roll = randbelow(total)
        if type(roll) is not int or not 0 <= roll < total:
            raise ValueError('Invalid random draw')
        cursor = roll
        for drop in pool:
            if cursor < drop.weight:
                after = 0 if drop.rarity == self.guaranteed_rarity else pity_before + 1
                return drop, after, guaranteed, roll, total
            cursor -= drop.weight
        raise AssertionError('Unreachable draw')


def funding_id(opening_id):
    return uuid5(opening_id,'case.funding')


def reward_id(opening_id,index):
    return uuid5(opening_id,'case.reward:'+str(index))


def validate_case_receipt(event,payload,receipt):
    if event == CASE_REWARD:
        validate_receipt(payload,receipt)
        return
    if (event not in (CASE_CONSUME,CASE_PAYMENT) or not isinstance(receipt,dict)
            or any(receipt.get(k) != v for k,v in payload.items())
            or receipt.get('outcome') not in ('APPLIED','REJECTED')):
        raise ValueError('Invalid case funding receipt')
    if event == CASE_CONSUME:
        if (type(receipt.get('quantity')) is not int or receipt.get('itemId') != 'case:'+payload['caseCode']
                or type(receipt.get('quantityAfter')) is not int or not 0 <= receipt['quantityAfter'] <= MAX_QUANTITY):
            raise ValueError('Invalid case debit receipt')
        reason = 'InsufficientCases'
    else:
        if (type(receipt.get('amount')) is not int or type(receipt.get('balanceAfter')) is not int
                or not 0 <= receipt['balanceAfter'] <= MAX_QUANTITY):
            raise ValueError('Invalid case payment receipt')
        if receipt['outcome'] == 'APPLIED':
            UUID(receipt['transactionId'])
        reason = 'InsufficientFunds'
    if receipt['outcome'] == 'REJECTED':
        available = receipt['quantityAfter'] if event == CASE_CONSUME else receipt['balanceAfter']
        required = payload['quantity'] if event == CASE_CONSUME else payload['amount']
        if receipt.get('code') != reason or available >= required:
            raise ValueError('Invalid rejection receipt')


def seeded_randbelow(seed, upper):
    """Unbiased deterministic draw from an opening's private, persisted CSPRNG seed."""
    if not isinstance(seed,bytes) or len(seed)!=32 or type(upper) is not int or not 1 <= upper <= MAX_QUANTITY:
        raise ValueError('Invalid case draw seed or range')
    limit = (1 << 256) - ((1 << 256) % upper)
    counter = 0
    while True:
        value = int.from_bytes(hmac.new(seed,b'case.draw.v1:'+counter.to_bytes(8,'big'),hashlib.sha256).digest(),'big')
        if value < limit:
            return value % upper
        counter += 1
