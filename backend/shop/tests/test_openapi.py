"""Examples must describe actual handlers, including retries and TEST-only routes."""
from datetime import datetime, timedelta, timezone
import json
from uuid import UUID, uuid4

from dishka import Provider, Scope, provide
from fastapi.testclient import TestClient
from fastapi.openapi.utils import get_openapi
from pydantic import ValidationError
import pytest

from vatican_shop.application.ports import PurchaseGateway, WeaponDefinitionsGateway, ImageStorage
from vatican_shop.application.errors import ParticipantConflict
from vatican_shop.application.mock_ports import RewardBundlesGateway, MockEntitlementGateway
from vatican_shop.application.mock_payments import VerifyMockPayments, FulfillMockPayments
from vatican_shop.application.use_cases import SettlePurchases
from vatican_shop.application.models import Principal
from vatican_shop.bootstrap.settings import Settings
from vatican_shop.presentation.app import create_app
from vatican_shop.presentation import examples as ex, response_schemas as models, schemas, mock_payments
from vatican_shop.presentation.documentation import OPERATIONS, ERROR_STATUSES
from vatican_shop.integrations.s3 import S3ImageStorage
from vatican_shop.persistence.postgres.tables import MockOrderState
from vatican_shop.domain.mock_payments import canonical
from vatican_inventory.application.use_cases import InventoryUseCases
from vatican_wallet.application.use_cases import WalletUseCases
from vatican_wallet.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as WalletUow
from .test_mock_payments import mock_world
from .test_media import FakeS3, IMAGE


def settings(url='postgresql://unused', enabled=False):
    return Settings(url, 'service-docs-test-' + 's' * 32, app_environment='test', mock_payments_enabled=enabled,
        mock_control_secret='c' * 32, mock_webhook_secret='w' * 32, mock_receipt_secret='r' * 32, mock_grant_secret='i' * 32)


@pytest.fixture(params=[False, True], ids=['prestige', 'mock-enabled'])
def docs_client(request):
    class Auth:
        def authenticate(self, token):return Principal(UUID(ex.ACCOUNT), False)
    with TestClient(create_app(settings(enabled=request.param), auth_gateway=Auth())) as client:
        yield client, request.param


def assert_response(spec, path, method, response):
    content = spec['paths'][path][method]['responses'][str(response.status_code)].get('content', {})
    if not content:
        assert not response.content
        return
    schema = content['application/json']['schema']
    choices = schema.get('oneOf', [schema])
    for choice in choices:
        model = getattr(models, choice['$ref'].rsplit('/', 1)[1])
        try:
            model.model_validate(response.json())
            return
        except ValidationError:
            continue
    pytest.fail('Actual response does not match any documented schema: ' + path + ' ' + response.text)


def test_docs_render_without_database_and_credentials_are_correct(docs_client):
    client, enabled = docs_client
    spec = client.get('/openapi.json').json()
    assert client.get('/docs').status_code == client.get('/redoc').status_code == 200
    assert '"filter": true' in client.get('/docs').text
    assert '"persistAuthorization": false' in client.get('/docs').text
    assert 'operationId' in spec['info']['description'] and 'CatalogChanged' in spec['info']['description']
    assert 'service-docs-test-' not in json.dumps(spec) and 'postgresql://unused' not in json.dumps(spec)
    security = spec['components']['securitySchemes']
    assert security['PlayerBearer']['scheme'] == 'bearer'
    native = get_openapi(title='Native route contract', version='1', routes=client.app.routes)
    assert set(spec['paths']) == set(native['paths'])
    expected = {key for key in OPERATIONS if enabled or '/mock/' not in key[0]}
    assert {(path, method) for path, methods in spec['paths'].items() for method in methods} == expected
    assert set(ERROR_STATUSES) == set(OPERATIONS)
    for path, methods in spec['paths'].items():
        for method, op in methods.items():
            assert op['summary'] and op['description'] and op['tags']
            assert (path, method) in OPERATIONS
            expected = [] if path.startswith('/health/') else [{'PlayerBearer': []}]
            if '/admin/shop/mock/' in path:
                expected = [{'PlayerBearer': [], 'MockControlKey': []}]
            elif path == '/v1/mock/provider/webhook':
                expected = [{'MockWebhookSignature': []}]
            assert op['security'] == expected
            assert not any(p['in'] == 'header' for p in op.get('parameters', []))
            # Descriptions/examples may change, but the actual route's parameter
            # names, locations, required flags and validation schemas must survive.
            documented = {(p['in'], p['name']): p for p in op.get('parameters', [])}
            for parameter in native['paths'][path][method].get('parameters', []):
                if parameter['in'] != 'header':
                    actual = documented[(parameter['in'], parameter['name'])]
                    assert {key: actual.get(key) for key in ('required', 'schema')} == {key: parameter.get(key) for key in ('required', 'schema')}
            native_body = native['paths'][path][method].get('requestBody', {}).get('content', {}).get('application/json')
            if native_body is not None:
                assert op['requestBody']['content']['application/json']['schema'] == native_body['schema']
            assert all(p.get('description') for p in op.get('parameters', []))
            for status, response in op['responses'].items():
                assert response['headers']['X-Request-ID']['schema']['format'] == 'uuid'
                if status == '503':
                    assert response['headers']['Retry-After']['example'] == '1'
    if enabled:
        assert security['MockControlKey']['name'] == 'X-Mock-Control-Key'
        assert security['MockWebhookSignature']['name'] == 'X-Mock-Signature'
        assert 'TIMEOUT' in spec['info']['description']
    else:
        assert not any('/mock/' in path for path in spec['paths'])
        assert not any(name.startswith('Mock') for name in spec['components']['schemas'])
        assert 'MOCK выключен' in spec['info']['description']


def test_all_references_and_examples_validate_against_documented_models(docs_client):
    client, _ = docs_client
    spec = client.get('/openapi.json').json()
    def resolve(node):
        if isinstance(node, dict):
            if '$ref' in node:
                target = spec
                for part in node['$ref'].removeprefix('#/').split('/'):
                    target = target[part]
            for value in node.values():
                resolve(value)
        elif isinstance(node, list):
            for value in node:
                resolve(value)
    resolve(spec)
    assert 'HTTPValidationError' not in spec['components']['schemas']
    for path, methods in spec['paths'].items():
        for op in methods.values():
            for response in op['responses'].values():
                content = response.get('content', {}).get('application/json')
                if content is None:
                    continue
                schema = content['schema']
                choices = schema.get('oneOf', [schema])
                for example in content['examples'].values():
                    accepted = 0
                    for choice in choices:
                        model = getattr(models, choice['$ref'].rsplit('/', 1)[1])
                        try:
                            model.model_validate(example['value'])
                            accepted += 1
                        except ValidationError:
                            pass
                    assert accepted == 1, (path, example)
            request = op.get('requestBody', {}).get('content', {}).get('application/json')
            if request is None:
                continue
            name = request['schema']['$ref'].rsplit('/', 1)[1]
            model = next(getattr(module, name) for module in (schemas, mock_payments, models) if hasattr(module, name))
            for example in request['examples'].values():
                model.model_validate(example['value'])
            for field in spec['components']['schemas'][name].get('properties', {}).values():
                # The raw webhook is validated by the domain; its response model documents its body.
                if name != 'MockWebhookBody':
                    assert field.get('description'), (name, field)


def test_upload_is_raw_binary_and_rejected_purchase_has_two_possible_409_bodies(docs_client):
    client, _ = docs_client
    spec = client.get('/openapi.json').json()
    upload = spec['paths']['/v1/admin/shop/images']['post']['requestBody']
    assert set(upload['content']) == {'image/png', 'image/jpeg'}
    assert all(value['schema']['format'] == 'binary' for value in upload['content'].values())
    conflict = spec['paths']['/v1/purchases']['post']['responses']['409']['content']['application/json']
    assert len(conflict['schema']['oneOf']) == 2
    assert {value['value'].get('status') for value in conflict['examples'].values()} == {'Rejected', None}


def test_error_examples_match_real_unauthorized_and_validation_responses(docs_client):
    client, _ = docs_client
    spec = client.get('/openapi.json').json()
    unauthorized = client.get('/v1/shop', headers={'X-Request-ID': ex.REQUEST})
    assert unauthorized.status_code == 401
    assert unauthorized.json() == spec['paths']['/v1/shop']['get']['responses']['401']['content']['application/json']['examples']['httpError']['value']
    assert_response(spec, '/v1/shop', 'get', unauthorized)
    for template, methods in spec['paths'].items():
        if template.startswith('/health/'):
            continue
        path = template.replace('{operation_id}', ex.OPERATION).replace('{account_id}', ex.ACCOUNT).replace('{offer_id}', 'sword')
        path = path.replace('{identity}', ex.PRODUCT_ID if '/products/' in path else ex.ORDER)
        for method, op in methods.items():
            kwargs = {}
            content = op.get('requestBody', {}).get('content', {})
            if 'application/json' in content:
                kwargs['json'] = next(iter(content['application/json']['examples'].values()))['value']
            elif 'image/png' in content:
                kwargs = {'content': IMAGE.read_bytes(), 'headers': {'Content-Type': 'image/png'}}
            denied = client.request(method, path, **kwargs)
            assert denied.status_code == 401, (method, path, denied.text)
            assert denied.headers['WWW-Authenticate'] == op['responses']['401']['headers']['WWW-Authenticate']['example']
            assert_response(spec, template, method, denied)
    validation = client.get('/v1/purchases?limit=101', headers={'Authorization': 'Bearer example-player', 'X-Request-ID': ex.REQUEST})
    assert validation.status_code == 422
    assert validation.json() == spec['paths']['/v1/purchases']['get']['responses']['422']['content']['application/json']['examples']['httpError']['value']
    missing = client.post('/v1/purchases', headers={'Authorization': 'Bearer example-player', 'X-Request-ID': ex.REQUEST})
    assert missing.status_code == 422
    assert missing.json() == spec['paths']['/v1/purchases']['post']['responses']['422']['content']['application/json']['examples']['httpError']['value']
    if '/v1/mock/provider/webhook' in spec['paths']:
        response = client.post('/v1/mock/provider/webhook', content=b'{}', headers={'X-Request-ID': ex.REQUEST})
        assert response.status_code == 401
        assert response.json() == spec['paths']['/v1/mock/provider/webhook']['post']['responses']['401']['content']['application/json']['examples']['httpError']['value']


def test_swagger_quickstarts_work_with_real_persistence_and_delivery(mock_world, wallet_storage):
    w = mock_world
    wallet = WalletUseCases(WalletUow(wallet_storage[0]))
    inventory = InventoryUseCases(w.iuow, w.definitions)
    class Delivery:
        conflict = False
        def charge(self, job):
            return wallet.change(job['accountId'], job['operationId'], job['plan']['price'])
        def grant(self, job):
            if self.conflict:raise ParticipantConflict()
            return inventory.grant(job['accountId'], job['operationId'], job['plan']['configurationId'])
        def refund(self, job):
            return wallet.change(job['accountId'], job['operationId'], job['plan']['price'], refund=True)
    delivery = Delivery()
    storage = S3ImageStorage(settings(w.url), client=FakeS3())
    class Overrides(Provider):
        @provide(scope=Scope.APP, provides=WeaponDefinitionsGateway)
        def definitions(self):return w.definitions
        @provide(scope=Scope.APP, provides=PurchaseGateway)
        def delivery(self):return delivery
        @provide(scope=Scope.APP, provides=ImageStorage)
        def images(self):return storage
        @provide(scope=Scope.APP, provides=RewardBundlesGateway)
        def bundles(self):return w.bundles
        @provide(scope=Scope.APP, provides=MockEntitlementGateway)
        def mock_delivery(self):return w.delivery
    with TestClient(create_app(settings(w.url, True), providers=(Overrides(),), auth_gateway=w.auth)) as client:
        spec = client.get('/openapi.json').json()
        admin = {'Authorization': 'Bearer admin'}
        player = {'Authorization': 'Bearer user'}
        control = {**admin, 'X-Mock-Control-Key': w.policy.control_secret}
        observed = set()
        def call(path, method='get', body=None, headers=player, template=None, status=200):
            response = client.request(method, path, json=body, headers=headers)
            assert response.status_code == status, response.text
            assert_response(spec, template or path, method, response)
            observed.add((template or path, method))
            return response.json() if response.content else None
        call('/health/live')
        call('/health/ready')
        created = call('/v1/admin/shop/offers', 'post', ex.OFFER_INPUT, admin, status=201)
        call('/v1/admin/shop/offers', headers=admin)
        call('/v1/admin/shop/offers/sword', headers=admin, template='/v1/admin/shop/offers/{offer_id}')
        uploaded = client.post('/v1/admin/shop/images', content=IMAGE.read_bytes(), headers={**admin, 'Content-Type': 'image/png'})
        assert uploaded.status_code == 201
        assert_response(spec, '/v1/admin/shop/images', 'post', uploaded)
        observed.add(('/v1/admin/shop/images', 'post'))
        catalog = call('/v1/shop')
        body = {**ex.PURCHASE_INPUT, 'catalogVersion': catalog['version']}
        call('/v1/purchases', 'post', body, status=202)
        worker = client.app.state.dishka_container.get(SettlePurchases)
        assert worker.once() and worker.once()
        receipt = call('/v1/purchases/' + ex.OPERATION, template='/v1/purchases/{operation_id}')
        assert receipt['status'] == 'Completed' and receipt['chargedAmount'] == 25
        assert call('/v1/purchases', 'post', body)['replayed']
        call('/v1/purchases')
        call('/v1/admin/shop/purchases', headers=admin)
        base = '/v1/admin/shop/accounts/' + str(w.auth.user) + '/purchases/' + ex.OPERATION
        call(base, headers=admin, template='/v1/admin/shop/accounts/{account_id}/purchases/{operation_id}')
        call(base + '/events', headers=admin, template='/v1/admin/shop/accounts/{account_id}/purchases/{operation_id}/events')
        updated = call('/v1/admin/shop/offers/sword', 'put', {**ex.OFFER_UPDATE,
            'expectedRevision': created['revision'], 'price': 30, 'purchaseLimit': 0, 'imageKey': uploaded.json()['imageKey']}, admin, '/v1/admin/shop/offers/{offer_id}')
        # Exercise the documented administrative recovery contract on a charged
        # purchase whose grant was not confirmed, then continue its original plan.
        delivery.conflict = True
        retry_id = str(uuid4())
        call('/v1/purchases', 'post', {**ex.PURCHASE_INPUT, 'operationId': retry_id,
             'catalogVersion': call('/v1/shop')['version']}, status=202)
        assert worker.once() and worker.once()
        retry_path = '/v1/admin/shop/accounts/' + str(w.auth.user) + '/purchases/' + retry_id
        detail = call(retry_path, headers=admin, template='/v1/admin/shop/accounts/{account_id}/purchases/{operation_id}')
        assert detail['canRetry']
        call(retry_path + '/retry', 'post', {**ex.RETRY_INPUT, 'expectedVersion': detail['job']['version']}, admin,
             '/v1/admin/shop/accounts/{account_id}/purchases/{operation_id}/retry')
        delivery.conflict = False
        assert worker.once()
        # Use an actually published Game bundle, then publish a new product version through the documented route.
        _, published = w.publish()
        mock_product = call('/v1/admin/shop/mock/products', 'post', {**ex.MOCK_PRODUCT_INPUT,
            'rewardBundleId': str(published['rewardBundleId']), 'version': 2, 'externalSku': 'test.sword.v2'}, control, status=201)
        call('/v1/mock/products')
        order_body = {**ex.MOCK_CREATE, 'productId': mock_product['productId']}
        order = call('/v1/mock/orders', 'post', order_body, status=202)
        identity = order['orderId']
        signal = call('/v1/admin/shop/mock/orders/' + identity + '/simulate', 'post',
            {'requestKey': ex.REQUEST, 'scenario': 'SUCCESS', 'providerUnavailable': False}, control,
            '/v1/admin/shop/mock/orders/{identity}/simulate')
        call('/v1/mock/orders/' + identity + '/confirm', 'post', {'receipt': signal['receipt']}, player,
             '/v1/mock/orders/{identity}/confirm', 202)
        raw = canonical(signal['webhook']['body'])
        webhook = client.post('/v1/mock/provider/webhook', content=raw, headers={'X-Mock-Signature': signal['webhook']['signature']})
        assert_response(spec, '/v1/mock/provider/webhook', 'post', webhook)
        assert webhook.status_code == 200
        observed.add(('/v1/mock/provider/webhook', 'post'))
        assert client.app.state.dishka_container.get(VerifyMockPayments).once()
        assert client.app.state.dishka_container.get(FulfillMockPayments).once()
        result = call('/v1/mock/orders/' + identity, template='/v1/mock/orders/{identity}')
        assert result['status'] == 'FULFILLED'
        assert call('/v1/mock/orders', 'post', order_body, status=202)['replayed']
        call('/v1/admin/shop/mock/orders/' + identity, headers=control, template='/v1/admin/shop/mock/orders/{identity}')
        # Only the verification budget is exhausted; no reward has been sent.
        # Verify that the documented retry response matches a real CAS recovery.
        pending = call('/v1/mock/orders', 'post', {**order_body, 'requestKey': str(uuid4())}, status=202)
        with w.db.transaction() as session:
            state = session.get(MockOrderState, UUID(pending['orderId']))
            state.attempts = 20
            state.available_at = datetime.now(timezone.utc) - timedelta(seconds=1)
        assert client.app.state.dishka_container.get(VerifyMockPayments).once()
        pending_path = '/v1/admin/shop/mock/orders/' + pending['orderId']
        detail = call(pending_path, headers=control, template='/v1/admin/shop/mock/orders/{identity}')
        call(pending_path + '/retry', 'post', {'requestKey': str(uuid4()), 'expectedVersion': detail['version'],
            'reason': 'Проверили провайдера; продолжаем сверку'}, control, '/v1/admin/shop/mock/orders/{identity}/retry')
        call('/v1/admin/shop/mock/products/' + mock_product['productId'] + '/availability', 'patch',
            {'enabled': False, 'requestKey': str(uuid4())}, control, '/v1/admin/shop/mock/products/{identity}/availability')
        call('/v1/admin/shop/offers/sword?expectedRevision=' + str(updated['revision']), 'delete', headers=admin,
             template='/v1/admin/shop/offers/{offer_id}', status=204)
        assert observed == set(OPERATIONS)
