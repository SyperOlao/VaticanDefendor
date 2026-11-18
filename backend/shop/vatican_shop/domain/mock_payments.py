"""TEST-only payment protocol and immutable weapon reward snapshots."""
import base64
from dataclasses import dataclass, field
from enum import StrEnum
import hashlib
import hmac
import json
import re
from uuid import UUID, NAMESPACE_URL, uuid5


class OrderStatus(StrEnum):
    PENDING = 'PENDING'
    UNKNOWN = 'UNKNOWN'
    VERIFIED = 'VERIFIED'
    DECLINED = 'DECLINED'
    FULFILLED = 'FULFILLED'
    REFUNDED = 'REFUNDED'


class ProviderStatus(StrEnum):
    PENDING = 'PENDING'
    VERIFIED = 'VERIFIED'
    DECLINED = 'DECLINED'
    REFUNDED = 'REFUNDED'


class FulfillmentStatus(StrEnum):
    NOT_READY = 'NOT_READY'
    PENDING = 'PENDING'
    PARTIAL = 'PARTIAL'
    COMPLETED = 'COMPLETED'
    CANCELLED = 'CANCELLED'
    REVIEW = 'REVIEW'


class UnitStatus(StrEnum):
    PENDING = 'PENDING'
    COMPLETED = 'COMPLETED'
    REVIEW = 'REVIEW'
    CANCELLED = 'CANCELLED'


class Scenario(StrEnum):
    SUCCESS = 'SUCCESS'
    DECLINED = 'DECLINED'
    PENDING = 'PENDING'
    TIMEOUT = 'TIMEOUT'
    REFUND = 'REFUND'
    DUPLICATE_WEBHOOK = 'DUPLICATE_WEBHOOK'


MOCK_PROVIDER = 'SHOP_DEV_MOCK'
MOCK_STOREFRONT = 'SHOP_DEV_MOCK'
TEST_ENVIRONMENT = 'TEST'
MAX_ATTEMPTS = 20


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(',', ':'), ensure_ascii=True, allow_nan=False).encode()


def digest(value):
    return hashlib.sha256(canonical(value)).digest()


def identifier(value):
    if not isinstance(value, UUID) or not value.int:
        raise ValueError('Invalid identifier')
    return value


def product_id(sku, version):
    return uuid5(NAMESPACE_URL, f'vatican.mock.product.v1:{sku}:{version}')


def transaction_id(order_id):
    return uuid5(order_id, 'mock.payment.transaction.v1')


def external_transaction_id(order_id):
    return 'MOCK-' + str(order_id)


def event_id(order_id, version):
    return uuid5(order_id, 'mock.payment.event:' + str(version))


def validate_plan(value, bundle_id):
    if (not isinstance(value, dict) or set(value) != {'schemaVersion', 'bundleId', 'items'}
            or type(value['schemaVersion']) is not int or value['schemaVersion'] != 1
            or value['bundleId'] != str(bundle_id) or not isinstance(value['items'], list)
            or not 1 <= len(value['items']) <= 20):
        raise ValueError('Invalid mock reward bundle')
    indices = set()
    for row in value['items']:
        if (not isinstance(row, dict) or set(row) != {'itemIndex', 'rewardType', 'rewardRef', 'quantity'}
                or type(row['itemIndex']) is not int or not 0 <= row['itemIndex'] <= 2_147_483_647
                or row['itemIndex'] in indices or row['rewardType'] != 'WEAPON'
                or not isinstance(row['rewardRef'], str) or re.fullmatch(r'[A-Za-z0-9_.-]{1,80}', row['rewardRef']) is None
                or type(row['quantity']) is not int or not 1 <= row['quantity'] <= 20):
            raise ValueError('Unsupported mock reward line')
        indices.add(row['itemIndex'])
    if sum(row['quantity'] for row in value['items']) > 20:
        raise ValueError('Too many mock reward units')
    return {**value, 'items': sorted(value['items'], key=lambda row: row['itemIndex'])}


@dataclass(frozen=True)
class MockPolicy:
    enabled: bool = False
    environment: str = 'production'
    control_secret: str = field(default='', repr=False)
    webhook_secret: str = field(default='', repr=False)
    receipt_secret: str = field(default='', repr=False)
    grant_secret: str = field(default='', repr=False)

    def __post_init__(self):
        if type(self.enabled) is not bool:
            raise ValueError('Invalid MOCK enable flag')
        if self.enabled:
            keys = (self.control_secret, self.webhook_secret, self.receipt_secret, self.grant_secret)
            if (self.environment not in ('test', 'development') or any(not isinstance(k, str) or not 32 <= len(k) <= 256 for k in keys)
                    or len(set(keys)) != len(keys)):
                raise ValueError('MOCK requires test/development and four distinct test secrets')

    def accepts_control(self, key):
        try:
            return isinstance(key, str) and len(key) <= 256 and hmac.compare_digest(key.encode(), self.control_secret.encode())
        except UnicodeError:return False

    def sign_webhook(self, body):
        return hmac.new(self.webhook_secret.encode(), body, hashlib.sha256).hexdigest()

    def check_webhook(self, body, signature):
        return isinstance(signature, str) and re.fullmatch(r'[a-f0-9]{64}',signature) is not None and hmac.compare_digest(
            self.sign_webhook(body), signature)

    def receipt(self, truth):
        data = canonical(truth)
        return base64.urlsafe_b64encode(data).decode().rstrip('=') + '.' + hmac.new(
            self.receipt_secret.encode(), data, hashlib.sha256).hexdigest()

    def parse_receipt(self, token):
        try:
            if not isinstance(token, str) or len(token) > 8192:
                raise ValueError()
            encoded, signature = token.split('.')
            data = base64.b64decode(encoded + '=' * (-len(encoded) % 4), altchars=b'-_', validate=True)
            if not hmac.compare_digest(hmac.new(self.receipt_secret.encode(), data, hashlib.sha256).hexdigest(), signature):
                raise ValueError()
            return json.loads(data)
        except (ValueError, TypeError, UnicodeError, RecursionError):
            raise ValueError('Invalid MOCK receipt') from None


def parse_event(body):
    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError()
            result[key] = value
        return result
    try:
        value = json.loads(body, object_pairs_hook=unique, parse_constant=lambda _: (_ for _ in ()).throw(ValueError()))
        if (not isinstance(value, dict) or set(value) != {'eventId', 'orderId', 'environment', 'providerVersion'}
                or value['environment'] != TEST_ENVIRONMENT or type(value['providerVersion']) is not int
                or not 1 <= value['providerVersion'] <= 2_147_483_647):
            raise ValueError()
        order, event = identifier(UUID(value['orderId'])), identifier(UUID(value['eventId']))
        if event != event_id(order, value['providerVersion']):
            raise ValueError()
        return {**value, 'orderId': str(order), 'eventId': str(event)}
    except (ValueError, TypeError, KeyError, AttributeError, RecursionError):
        raise ValueError('Invalid MOCK event') from None


def validate_truth(truth, snapshot, order_id, account_id):
    expected = {'orderId': str(order_id), 'accountId': str(account_id), 'environment': TEST_ENVIRONMENT,
                'externalSku': snapshot['externalSku'], 'priceMinor': snapshot['priceMinor'],
                'currencyIso': snapshot['currencyIso'], 'transactionId': external_transaction_id(order_id)}
    if (not isinstance(truth, dict) or set(truth) != set(expected) | {'status', 'providerVersion'}
            or any(type(truth.get(k)) is not type(v) or truth[k] != v for k, v in expected.items())
            or truth.get('status') not in ProviderStatus or type(truth.get('providerVersion')) is not int
            or not 1 <= truth['providerVersion'] <= 2_147_483_647):
        raise ValueError('Provider payment does not match this order')
    return truth


def grant_payload(job):
    return {'accountId': str(job['accountId']), 'transactionId': str(job['transactionId']),
            'lineIndex': job['lineIndex'], 'unitIndex': job['unitIndex'], 'configurationId': job['configurationId']}


def validate_grant(job, receipt):
    expected = grant_payload(job)
    if (not isinstance(receipt, dict) or receipt.get('status') != 'APPLIED'
            or any(type(receipt.get(k)) is not type(v) or receipt[k] != v for k, v in expected.items())
            or not isinstance(receipt.get('instanceId'), str) or not UUID(receipt['instanceId']).int
            or type(receipt.get('inventoryRevision')) is not int or not 1 <= receipt['inventoryRevision'] <= 9_223_372_036_854_775_807):
        raise ValueError('Invalid MOCK entitlement acknowledgement')
    return receipt
