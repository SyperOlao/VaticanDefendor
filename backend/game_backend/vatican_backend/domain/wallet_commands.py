"""Wallet command contract, independent of wallet implementation."""
import hashlib
import json
from .level_rewards import validate_receipt as level_receipt
from .cases import validate_case_receipt
from .prestige import validate_receipt as prestige_receipt

EVENTS = {'run.currency_reward', 'level.reward', 'case.reward', 'case.payment', 'prestige-upgrade.payment'}


def command(message):
    if (message['destination'] != 'wallet' or message['event_name'] not in EVENTS
            or type(message['schema_version']) is not int or message['schema_version'] != 1):
        raise ValueError('Unsupported wallet command')
    return {'messageId': str(message['id']), 'eventName': message['event_name'],
            'schemaVersion': message['schema_version'], 'payload': message['payload']}


def result(message, body):
    digest = hashlib.sha256(json.dumps(command(message), sort_keys=True, separators=(',', ':'), allow_nan=False).encode()).hexdigest()
    if (not isinstance(body, dict) or body.get('messageId') != str(message['id'])
            or body.get('payloadSha256') != digest
            or type(body.get('deliveryGeneration')) is not int or body['deliveryGeneration'] < 1
            or body.get('status') not in ('PENDING', 'APPLIED', 'REJECTED', 'REVIEW')):
        raise ValueError('Wallet source mismatch')
    state, value = body['status'], body.get('result')
    if state == 'PENDING':
        if value is not None:raise ValueError('Unconfirmed command has a result')
        return state, None
    if not isinstance(value, dict) or value.get('messageId') != str(message['id']) or value.get('status') != state:
        raise ValueError('Wallet result mismatch')
    if state != 'APPLIED':return state, None
    receipt, payload, event = value.get('receipt'), message['payload'], message['event_name']
    if not isinstance(receipt, dict):raise ValueError('Missing wallet receipt')
    if event == 'run.currency_reward':
        if (any(type(receipt.get(key)) is not type(value) or receipt[key] != value for key, value in payload.items())
                or receipt.get('currencyId') != 'PRESTIGE' or type(receipt.get('balanceAfter')) is not int
                or receipt['balanceAfter'] < 0):
            raise ValueError('Run receipt mismatch')
    elif event == 'level.reward':level_receipt(payload, receipt)
    elif event == 'prestige-upgrade.payment':prestige_receipt(payload, receipt)
    else:validate_case_receipt(event, payload, receipt)
    return state, receipt
