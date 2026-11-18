"""Inventory protocol validation without imports of the inventory implementation."""
import hashlib
import json
from uuid import UUID
from .level_rewards import validate_receipt as level_receipt
from .cases import validate_case_receipt

EVENTS = {'run.weapon_reward', 'level.reward', 'case.reward', 'case.consume', 'inventory.item.consume'}


def command(message):
    if message['destination'] != 'inventory' or message['event_name'] not in EVENTS or type(message['schema_version']) is not int or message['schema_version'] != 1:
        raise ValueError('Unsupported inventory command')
    value = {'messageId': str(message['id']), 'eventName': message['event_name'],
             'schemaVersion': message['schema_version'], 'payload': message['payload']}
    if len(json.dumps(value, allow_nan=False).encode()) > 262_144:
        raise ValueError('Inventory command is too large')
    return value


def command_hash(message):
    return hashlib.sha256(json.dumps(command(message), sort_keys=True, separators=(',', ':'), allow_nan=False).encode()).hexdigest()


def result(message, body):
    if (not isinstance(body, dict) or body.get('messageId') != str(message['id'])
            or body.get('payloadSha256') != command_hash(message)
            or type(body.get('deliveryGeneration')) is not int or body['deliveryGeneration'] < 1
            or body.get('status') not in ('PENDING', 'APPLIED', 'REJECTED', 'REVIEW')):
        raise ValueError('Inventory source mismatch')
    status, value = body['status'], body.get('result')
    if status == 'PENDING':
        if value is not None:raise ValueError('Unconfirmed command has a result')
        return status, None
    if not isinstance(value, dict) or value.get('messageId') != str(message['id']) or value.get('status') != status:
        raise ValueError('Inventory result mismatch')
    if status != 'APPLIED':return status, None
    receipt, payload, event = value.get('receipt'), message['payload'], message['event_name']
    if not isinstance(receipt, dict):raise ValueError('Invalid inventory receipt')
    if event == 'run.weapon_reward':
        if any(receipt.get(key) != payload[key] for key in ('accountId', 'runId', 'configurationId')):
            raise ValueError('Run receipt mismatch')
        if not UUID(receipt['instanceId']).int:raise ValueError('Invalid reward instance')
    elif event == 'level.reward':level_receipt(payload, receipt)
    elif event in ('case.reward', 'case.consume'):validate_case_receipt(event, payload, receipt)
    else:
        if (any(receipt.get(k) != v for k, v in payload.items()) or type(receipt.get('quantity')) is not int
                or receipt.get('status') not in ('APPLIED', 'REJECTED')
                or type(receipt.get('quantityAfter')) is not int or receipt['quantityAfter'] < 0):
            raise ValueError('Item receipt mismatch')
        if receipt['status'] == 'REJECTED' and (receipt.get('code') != 'InsufficientItems' or receipt['quantityAfter'] >= payload['quantity']):
            raise ValueError('Invalid item rejection')
    if 'inventoryRevision' in receipt and (type(receipt['inventoryRevision']) is not int or not 1 <= receipt['inventoryRevision'] <= 9_223_372_036_854_775_807):
        raise ValueError('Invalid inventory revision')
    return status, receipt
