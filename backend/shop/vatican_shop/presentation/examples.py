"""Illustrative data, never credentials or automatic content publication."""
from uuid import UUID

from ..domain.mock_payments import event_id, product_id, transaction_id

ACCOUNT = '11111111-1111-4111-8111-111111111111'
OPERATION = '22222222-2222-4222-8222-222222222222'
REQUEST = '33333333-3333-4333-8333-333333333333'
INSTANCE = '44444444-4444-4444-8444-444444444444'
BUNDLE = '55555555-5555-4555-8555-555555555555'
ORDER = '66666666-6666-4666-8666-666666666666'
AT = '2026-11-17T12:00:00Z'
VERSION = 'shop-' + 'a' * 48
IMAGE_KEY = 'shop/items/' + 'a' * 64 + '.png'
IMAGE_URL = 'https://media.example.com/vatican-shop-media/' + IMAGE_KEY
OFFER_INPUT = {'id': 'sword', 'weaponConfigurationId': 'sword.rare', 'title': 'Редкий меч',
               'price': 25, 'purchaseLimit': 1, 'available': True, 'unavailableReason': ''}
OFFER_UPDATE = {key: value for key, value in OFFER_INPUT.items() if key != 'id'} | {'expectedRevision': 1}
ADMIN_OFFER = {**OFFER_INPUT, 'currencyId': 'prestige', 'imageKey': None, 'imageUrl': None,
               'revision': 1, 'createdAt': AT, 'updatedAt': AT, 'deletedAt': None}
CATALOG = {'version': VERSION, 'currencyId': 'prestige', 'revision': 1,
    'offers': [{**OFFER_INPUT, 'currencyId': 'prestige', 'imageKey': None, 'imageUrl': None, 'purchasedCount': 0}],
    'configurations': [{'Id': 'sword.rare', 'ExperiencePerLevel': 100, 'MaximumLevel': 10}]}
PURCHASE_INPUT = {'operationId': OPERATION, 'offerId': 'sword', 'catalogVersion': VERSION}
ANALYTICS = {'status': 'SKIPPED', 'messageId': None, 'receipt': None}
PENDING = {'operationId': OPERATION, 'offerId': 'sword', 'status': 'Pending', 'replayed': False,
           'analyticsStatus': 'SKIPPED', 'analytics': ANALYTICS}
COMPLETED = {**PENDING, 'status': 'Completed', 'chargedAmount': 25, 'currencyId': 'PRESTIGE',
    'grantedWeaponInstanceIds': [INSTANCE], 'inventoryRevision': 2, 'completedAt': AT,
    'analyticsStatus': 'PENDING', 'analytics': {'status': 'PENDING', 'messageId': REQUEST,
                                             'attempts': 0, 'lastError': None, 'receipt': None}}
REJECTED = {**PENDING, 'status': 'Rejected', 'code': 'InsufficientFunds'}
REVIEW = {**PENDING, 'status': 'Review', 'code': 'ParticipantConflict'}
JOB = {'state': 'REVIEW', 'version': 5, 'attempts': 1, 'lastErrorCode': 'ParticipantConflict',
       'leaseExpiresAt': None, 'nextAttemptAt': None}
ADMIN_SUMMARY = {'accountId': ACCOUNT, 'operationId': OPERATION, 'offerId': 'sword', 'createdAt': AT,
                 'status': 'Review', 'job': JOB, 'canRetry': True}
ADMIN_DETAIL = {**ADMIN_SUMMARY, 'catalogVersion': VERSION, 'receipt': REVIEW,
                'plan': {'price': 25, 'configurationId': 'sword.rare'}, 'resumeState': 'CHARGED'}
RETRY_INPUT = {'expectedVersion': 5, 'requestKey': REQUEST, 'reason': 'Сверили выдачу в inventory; зависимость восстановлена'}
RETRY_RESULT = {'accountId': ACCOUNT, 'operationId': OPERATION, 'status': 'Pending', 'jobVersion': 6, 'replayed': False}
EVENT = {'sequence': 4, 'kind': 'REVIEW', 'fromState': 'CHARGED', 'state': 'REVIEW', 'attempts': 1,
         'errorCode': 'ParticipantConflict', 'actorId': None, 'requestKey': None, 'reason': None, 'createdAt': AT}
IMAGE = {'imageKey': IMAGE_KEY, 'imageUrl': IMAGE_URL, 'contentType': 'image/png',
         'sizeBytes': 145000, 'width': 1024, 'height': 1024}
MOCK_PRODUCT_INPUT = {'sku': 'test.sword', 'version': 1, 'externalSku': 'test.sword.v1',
                      'rewardBundleId': BUNDLE, 'priceMinor': 199, 'currencyIso': 'USD'}
PRODUCT_ID = str(product_id('test.sword', 1))
MOCK_SNAPSHOT = {**MOCK_PRODUCT_INPUT, 'productId': PRODUCT_ID, 'rewardSnapshot': {
    'schemaVersion': 1, 'bundleId': BUNDLE,
    'items': [{'itemIndex': 0, 'rewardType': 'WEAPON', 'rewardRef': 'sword.rare', 'quantity': 1}]}}
MOCK_PRODUCT = {**MOCK_SNAPSHOT, 'enabled': True, 'replayed': False}
MOCK_CREATE = {'productId': PRODUCT_ID, 'requestKey': REQUEST}
MOCK_ORDER = {'orderId': ORDER, 'accountId': ACCOUNT, 'environment': 'TEST', 'status': 'PENDING',
    'fulfillmentStatus': 'NOT_READY', 'version': 1, 'transactionId': None, 'providerVersion': 0,
    'reviewCode': None, 'createdAt': AT, 'product': MOCK_SNAPSHOT, 'units': [], 'replayed': False}
MOCK_TX = str(transaction_id(UUID(ORDER)))
MOCK_UNIT = {'lineIndex': 0, 'unitIndex': 0, 'state': 'COMPLETED', 'attempts': 1, 'lastError': None,
    'receipt': {'accountId': ACCOUNT, 'transactionId': MOCK_TX, 'lineIndex': 0, 'unitIndex': 0,
        'configurationId': 'sword.rare', 'status': 'APPLIED', 'instanceId': INSTANCE, 'inventoryRevision': 2}}
MOCK_FULFILLED = {**MOCK_ORDER, 'status': 'FULFILLED', 'fulfillmentStatus': 'COMPLETED', 'version': 6,
                  'transactionId': MOCK_TX, 'providerVersion': 2, 'units': [MOCK_UNIT]}
MOCK_REFUNDED = {**MOCK_FULFILLED, 'status': 'REFUNDED', 'fulfillmentStatus': 'REVIEW', 'version': 9,
                 'providerVersion': 3, 'reviewCode': 'RefundAfterDispatch'}
WEBHOOK = {'orderId': ORDER, 'eventId': str(event_id(UUID(ORDER), 2)), 'environment': 'TEST', 'providerVersion': 2}
SIMULATED = {'orderId': ORDER, 'scenario': 'SUCCESS', 'replayed': False, 'sdkStatus': 'VERIFIED',
    'receipt': '<receipt из настоящего ответа simulate>',
    'webhook': {'body': WEBHOOK, 'signature': '0' * 64}}


def example(summary, value):
    return {'summary': summary, 'value': value}
