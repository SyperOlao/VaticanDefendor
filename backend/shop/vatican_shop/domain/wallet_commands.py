"""Stable purchase command IDs and strict saved result validation."""
import hashlib
import json
from uuid import uuid5

EVENTS = {False: 'wallet.purchase.charge', True: 'wallet.purchase.refund'}
REJECTIONS = {False: 'InsufficientFunds', True: 'ChargeNotFound'}


def command(job, refund=False):
    event = EVENTS[refund]
    identity = uuid5(job['accountId'], event + '.v1:' + str(job['operationId']))
    return {'messageId': str(identity), 'eventName': event, 'schemaVersion': 1,
            'payload': {'accountId': str(job['accountId']), 'operationId': str(job['operationId']),
                        'amount': job['plan']['price']}}


def result(job, body, refund=False):
    envelope = command(job, refund)
    identity = envelope['messageId']
    digest = hashlib.sha256(json.dumps(envelope, sort_keys=True, separators=(',', ':'), allow_nan=False).encode()).hexdigest()
    if (not isinstance(body, dict) or body.get('messageId') != identity or body.get('payloadSha256') != digest
            or type(body.get('deliveryGeneration')) is not int or body['deliveryGeneration'] < 1
            or body.get('status') not in ('PENDING', 'APPLIED', 'REJECTED', 'REVIEW')):
        raise ValueError('Wallet command mismatch')
    state, saved = body['status'], body.get('result')
    if state == 'PENDING':
        if saved is not None:raise ValueError('Unconfirmed wallet result')
        return state, None
    if not isinstance(saved, dict) or saved.get('messageId') != identity or saved.get('status') != state:
        raise ValueError('Wallet result mismatch')
    if state != 'APPLIED':return state, None
    receipt = saved.get('receipt')
    if (not isinstance(receipt, dict) or receipt.get('status') not in ('APPLIED', 'REJECTED')
            or receipt.get('currencyId') != 'PRESTIGE'
            or any(type(receipt.get(key)) is not type(value) or receipt[key] != value
                   for key, value in envelope['payload'].items())):
        raise ValueError('Wallet purchase receipt mismatch')
    if receipt['status'] == 'REJECTED' and receipt.get('code') != REJECTIONS[refund]:
        raise ValueError('Unknown purchase rejection')
    if receipt['status'] == 'APPLIED' and (type(receipt.get('balance')) is not int or receipt['balance'] < 0):
        raise ValueError('Invalid wallet balance')
    return state, receipt
