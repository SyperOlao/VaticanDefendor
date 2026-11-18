"""Versioned rating command/result contract, independent of transport and ORM."""
from dataclasses import dataclass
import hashlib
import json
import re
from uuid import UUID, NAMESPACE_URL, uuid5

MAX_BODY_BYTES = 16_384
RESULT_EVENT = 'rating.command.result'


def command_envelope(message):
    return {'messageId': str(message['id']), 'eventName': message['event_name'],
            'schemaVersion': message['schema_version'], 'payload': message['payload']}


def command_hash(message):
    return hashlib.sha256(json.dumps(command_envelope(message), sort_keys=True,
        separators=(',', ':'), allow_nan=False).encode()).hexdigest()


def _object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError()
        result[key] = value
    return result


@dataclass(frozen=True)
class RatingResult:
    event_id: UUID
    message_id: UUID
    generation: int
    command_hash: str
    status: str
    receipt: dict | None
    code: str | None
    digest: bytes


def parse_result(body, property_id=None, correlation_id=None, content_type=None, content_encoding=None):
    try:
        if (len(body) > MAX_BODY_BYTES or content_type not in (None, 'application/json')
                or content_encoding not in (None, 'utf-8')):
            raise ValueError()
        envelope = json.loads(body.decode('utf-8'), object_pairs_hook=_object,
            parse_constant=lambda _: (_ for _ in ()).throw(ValueError()))
        if (not isinstance(envelope, dict) or set(envelope) != {'messageId','eventName','schemaVersion','payload'}
                or envelope['eventName'] != RESULT_EVENT or type(envelope['schemaVersion']) is not int
                or envelope['schemaVersion'] != 1):
            raise ValueError()
        identity = UUID(envelope['messageId'])
        payload = envelope['payload']
        if not isinstance(payload, dict):
            raise ValueError()
        source = UUID(payload['messageId'])
        status, generation = payload['status'], payload['deliveryGeneration']
        keys = {'messageId', 'status', 'deliveryGeneration', 'commandHash'}
        if status == 'APPLIED':
            keys.add('receipt')
            if not isinstance(payload.get('receipt'), dict):
                raise ValueError()
        elif status in ('REJECTED', 'REVIEW'):
            keys.add('code')
            if payload.get('code') not in ('CommandRejected', 'CommandConflict', 'HandlerUnavailable', 'HandlerFailed'):
                raise ValueError()
        else:
            raise ValueError()
        if (set(payload) != keys or type(generation) is not int or not 1 <= generation <= 2_147_483_647
                or not isinstance(payload['commandHash'], str)
                or re.fullmatch(r'[0-9a-f]{64}', payload['commandHash']) is None
                or identity != uuid5(source, f'rating.broker:rabbit.results:{generation}')
                or (property_id is not None and UUID(property_id) != identity)
                or (correlation_id is not None and UUID(correlation_id) != source)):
            raise ValueError()
        canonical = json.dumps(envelope, sort_keys=True, separators=(',', ':'), allow_nan=False).encode()
        return RatingResult(identity, source, generation, payload['commandHash'], status,
                            payload.get('receipt'), payload.get('code'), hashlib.sha256(canonical).digest())
    except (ValueError, TypeError, KeyError, AttributeError, UnicodeError, RecursionError, OverflowError):
        raise ValueError('Invalid rating result') from None


def quarantine_id(body, metadata):
    digest = hashlib.sha256(json.dumps(metadata, default=str, ensure_ascii=True).encode())
    digest.update(body)
    return uuid5(NAMESPACE_URL, 'vatican.game.rating.rejection:' + digest.hexdigest()), digest.digest()
