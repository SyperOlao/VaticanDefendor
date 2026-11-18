"""Once-only analytics for a confirmed prestige purchase."""
from enum import StrEnum
import hashlib
import json
import re
from uuid import UUID, uuid5

PURCHASE_EVENT='shop.purchase.analytics'
MAX_DELIVERY_ATTEMPTS=20


class AnalyticsStatus(StrEnum):
    SKIPPED='SKIPPED'
    PENDING='PENDING'
    COMPLETED='COMPLETED'
    REVIEW='REVIEW'


def purchase_id(account_id,operation_id):
    return uuid5(account_id,'shop.purchase:'+str(operation_id))


def message_id(account_id,operation_id):
    return uuid5(purchase_id(account_id,operation_id),'analytics.shop.purchase.v1')


def payload_for(account_id,operation_id,offer_id,catalog_version,plan,receipt,completed_at):
    amount=receipt.get('chargedAmount');instances=receipt.get('grantedWeaponInstanceIds')
    if (receipt.get('status')!='Completed' or receipt.get('currencyId')!='PRESTIGE'
            or type(amount) is not int or not 0<=amount<=9_223_372_036_854_775_807 or amount!=plan['price']
            or not isinstance(instances,list) or len(instances)!=1
            or not isinstance(instances[0],str) or UUID(instances[0]).int==0
            or not isinstance(offer_id,str) or re.fullmatch(r'[a-z0-9_.-]{1,64}',offer_id) is None
            or not isinstance(catalog_version,str) or re.fullmatch(r'[A-Za-z0-9_.-]{1,64}',catalog_version) is None
            or not isinstance(plan['configurationId'],str) or re.fullmatch(r'[A-Za-z0-9_.-]{1,80}',plan['configurationId']) is None):
        raise ValueError('Invalid confirmed purchase snapshot')
    return {'purchaseId':str(purchase_id(account_id,operation_id)),'accountId':str(account_id),'operationId':str(operation_id),
        'offerId':offer_id,'catalogVersion':catalog_version,'currencyId':'PRESTIGE','chargedAmount':amount,
        'weaponConfigurationId':plan['configurationId'],'weaponInstanceIds':list(instances),'status':'Completed',
        'completedAt':completed_at.isoformat()}


def expected_receipt(payload):
    digest=hashlib.sha256(json.dumps([PURCHASE_EVENT,1,payload],sort_keys=True,separators=(',',':')).encode()).hexdigest()
    return {'purchaseId':payload['purchaseId'],'accountId':payload['accountId'],'payloadHash':digest,
        'eventIds':[str(uuid5(UUID(payload['purchaseId']),'analytics.shop.purchase_confirmed.v1'))]}


def validate_receipt(payload,receipt):
    if not isinstance(receipt,dict) or receipt!=expected_receipt(payload):
        raise ValueError('Invalid purchase analytics acknowledgement')
