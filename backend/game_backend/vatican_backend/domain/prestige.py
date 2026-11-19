"""Permanent cumulative percentage bonuses, pinned per purchase and per run."""
from copy import deepcopy
from decimal import Decimal
from enum import StrEnum
import re
from uuid import UUID,uuid5

PAYMENT_EVENT='prestige-upgrade.payment'
REFUND_EVENT = 'prestige-upgrade.refund'
MAX_AMOUNT=9_223_372_036_854_775_807
IDENTIFIER=re.compile(r'[A-Za-z0-9_.-]{1,64}')
SUPPORTED={'HERO':{'MOVE_SPEED','ATTACK_SPEED','HEALTH','ATTACK_DAMAGE'},
           'ARCHER':{'ATTACK_SPEED','HEALTH','ATTACK_DAMAGE'},'WALL':{'HEALTH'}}


class PurchaseStatus(StrEnum):
    PENDING='PENDING'
    COMPLETED='COMPLETED'
    REJECTED='REJECTED'
    REVIEW='REVIEW'


def bonus(value):
    try:
        if not isinstance(value,str):raise ValueError()
        amount=Decimal(value)
        if not amount.is_finite() or not 0<amount<=5 or amount!=amount.quantize(Decimal('.000001')):raise ValueError()
        return amount
    except (ValueError,ArithmeticError):raise ValueError('Invalid prestige bonus') from None


class PrestigeRules:
    def __init__(self,value):
        if (not isinstance(value,dict) or set(value)!={'schemaVersion','rulesetId','upgrades'}
                or type(value['schemaVersion']) is not int or value['schemaVersion']!=1
                or not isinstance(value['rulesetId'],str) or re.fullmatch(r'[A-Za-z0-9_.-]{1,128}',value['rulesetId']) is None
                or not isinstance(value['upgrades'],list) or not 1<=len(value['upgrades'])<=sum(len(s) for s in SUPPORTED.values())):
            raise ValueError('Invalid prestige rules')
        entries=[];codes=set();pairs=set()
        for item in value['upgrades']:
            if (not isinstance(item,dict) or set(item)!={'code','target','stat','levels'}
                    or not isinstance(item['code'],str) or IDENTIFIER.fullmatch(item['code']) is None
                    or not isinstance(item['target'],str) or item['target'] not in SUPPORTED
                    or not isinstance(item['stat'],str) or item['stat'] not in SUPPORTED[item['target']]
                    or item['code'] in codes or (item['target'],item['stat']) in pairs
                    or not isinstance(item['levels'],list) or not 1<=len(item['levels'])<=5):raise ValueError('Invalid prestige upgrade')
            codes.add(item['code']);pairs.add((item['target'],item['stat']));levels=[];previous=Decimal(0)
            for index,row in enumerate(item['levels'],1):
                if (not isinstance(row,dict) or set(row)!={'level','cost','modifierValue'}
                        or type(row['level']) is not int or row['level']!=index
                        or type(row['cost']) is not int or not 1<=row['cost']<=MAX_AMOUNT):raise ValueError('Invalid prestige level')
                amount=bonus(row['modifierValue'])
                if amount<=previous:raise ValueError('Bonuses must increase with level')
                previous=amount;levels.append({**row,'modifierValue':format(amount,'.6f')})
            entries.append({**item,'levels':levels})
        self._value={'schemaVersion':1,'rulesetId':value['rulesetId'],'upgrades':sorted(entries,key=lambda x:x['code'])}

    def snapshot(self):return deepcopy(self._value)
    def upgrade(self,code):return next((deepcopy(i) for i in self._value['upgrades'] if i['code']==code),None)


def bonuses(entries):
    """Only the latest purchased level contributes; levels are not added again."""
    modifiers=[];pairs=set()
    for item in sorted(entries,key=lambda i:i['code']):
        target,stat=item['target'],item['stat']
        if target not in SUPPORTED or stat not in SUPPORTED[target] or (target,stat) in pairs:raise ValueError('Invalid stored prestige bonus')
        pairs.add((target,stat))
        modifiers.append({'target':target,'stat':stat,'multiplier':format(1+bonus(item['modifierValue']),'.6f')})
    return {'schemaVersion':1,'upgrades':deepcopy(entries),'modifiers':modifiers}


def payment_id(purchase):return uuid5(purchase,'prestige.payment.v1')


def refund_id(reset):
    return uuid5(reset, 'prestige.refund.v1')


def validate_refund_receipt(payload, receipt):
    if (not isinstance(receipt, dict) or any(receipt.get(key) != value for key, value in payload.items())
            or type(receipt.get('amount')) is not int or type(receipt.get('balanceAfter')) is not int
            or not 0 <= receipt['balanceAfter'] <= MAX_AMOUNT):
        raise ValueError('Invalid prestige refund acknowledgement')
    if receipt.get('outcome') == 'APPLIED' and receipt['balanceAfter'] >= payload['amount']:
        return
    if (receipt.get('outcome') == 'REJECTED' and receipt.get('rejectionCode') == 'WalletCapacityExceeded'
            and receipt['balanceAfter'] + payload['amount'] > MAX_AMOUNT):
        return
    raise ValueError('Invalid prestige refund outcome')


def validate_receipt(payload,receipt):
    if (not isinstance(receipt,dict) or any(receipt.get(k)!=v for k,v in payload.items())
            or type(receipt.get('amount')) is not int or type(receipt.get('fromLevel')) is not int
            or type(receipt.get('toLevel')) is not int or type(receipt.get('balanceAfter')) is not int
            or not 0<=receipt['balanceAfter']<=MAX_AMOUNT):raise ValueError('Invalid prestige payment acknowledgement')
    if receipt.get('outcome')=='APPLIED':
        if not isinstance(receipt.get('transactionId'),str) or UUID(receipt['transactionId']).int==0:raise ValueError('Invalid transaction')
    elif receipt.get('outcome')=='REJECTED':
        if receipt.get('rejectionCode')!='InsufficientFunds' or receipt['balanceAfter']>=payload['amount']:raise ValueError('Invalid payment refusal')
    else:raise ValueError('Invalid payment outcome')
