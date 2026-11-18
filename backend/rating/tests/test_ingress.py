import asyncio
import json

from fastapi.testclient import TestClient
import pytest

from vatican_rating.application.access import AuthenticatePlayer
from vatican_rating.application.errors import ApplicationError
from vatican_rating.bootstrap.settings import Settings
from vatican_rating.presentation.app import create_app


SECRET = 's' * 40


def assert_safe(response, status):
    assert response.status_code == status
    assert set(response.json()) == {'code', 'detail', 'requestId', 'errors'}
    assert response.headers['X-Request-ID'] == response.json()['requestId']
    assert response.headers['Cache-Control'] == 'no-store'
    if status in (401, 408, 413):
        assert response.headers['Connection'] == 'close'


@pytest.mark.parametrize('secret', [None, 'wrong', b'\xff' * 40])
def test_internal_auth_is_checked_before_parsing_json(secret):
    with TestClient(create_app(Settings('postgresql://unused', SECRET))) as client:
        headers = {'X-Service-Secret': secret} if secret is not None else {}
        response = client.post('/internal/messages', content=b'{broken-private-json', headers=headers)
        assert_safe(response, 401)
        assert 'private' not in response.text


@pytest.mark.parametrize('token', ['\xff', 'bad\x00token', 'bad\x1ftoken', '\x7f'])
def test_non_ascii_or_control_token_is_rejected_before_auth_call(token):
    class Auth:
        def authenticate(self, token):
            pytest.fail('Malformed bearer token reached auth')

    with pytest.raises(ApplicationError) as error:
        AuthenticatePlayer(Auth()).execute('Bearer ' + token)
    assert error.value.status_code == 401


def test_actual_streamed_size_is_bounded_without_trusting_content_length():
    async def request(app, headers):
        consumed = []
        output = []

        async def receive():
            consumed.append(True)
            assert len(consumed) <= 5, 'Application read past the message size limit'
            return {'type': 'http.request', 'body': b'x' * 4096, 'more_body': True}

        async def send(message):
            output.append(message)

        scope = {'type': 'http', 'asgi': {'version': '3.0'}, 'http_version': '1.1',
                 'method': 'POST', 'scheme': 'http', 'path': '/internal/messages', 'raw_path': b'/internal/messages',
                 'query_string': b'', 'headers': headers, 'client': ('127.0.0.1', 1), 'server': ('test', 80)}
        await asyncio.wait_for(app(scope, receive, send), timeout=2)
        assert len(consumed) <= 5
        assert output[0]['status'] == 413
        body = json.loads(b''.join(part.get('body', b'') for part in output[1:]))
        assert body['code'] == 'RequestTooLarge'

    with TestClient(create_app(Settings('postgresql://unused', SECRET))) as client:
        base = [(b'x-service-secret', SECRET.encode()), (b'content-type', b'application/json')]
        for headers in (base, [*base, (b'content-length', b'1')]):
            asyncio.run(request(client.app, headers))


def test_message_validation_reports_safe_rating_field_names():
    with TestClient(create_app(Settings('postgresql://unused', SECRET))) as client:
        response = client.post('/internal/messages', headers={'X-Service-Secret': SECRET}, json={
            'messageId': 'private-invalid-uuid', 'eventName': 'run.rating', 'schemaVersion': 1, 'payload': {},
        })
        assert_safe(response, 422)
        assert response.json()['errors'] == [
            {'field': 'body.messageId', 'type': 'uuid_parsing', 'message': 'Invalid UUID'},
        ]
        assert 'private' not in response.text


def test_stalled_body_read_has_a_deadline_and_never_opens_a_database(monkeypatch):
    import vatican_rating.presentation.ingress as ingress

    monkeypatch.setattr(ingress, 'MESSAGE_READ_TIMEOUT_SECONDS', .02)

    async def request(app):
        output = []

        async def receive():
            await asyncio.sleep(10)
            raise AssertionError('Stalled read was not cancelled')

        async def send(message):
            output.append(message)

        scope = {'type': 'http', 'asgi': {'version': '3.0'}, 'http_version': '1.1',
                 'method': 'POST', 'scheme': 'http', 'path': '/internal/messages', 'raw_path': b'/internal/messages',
                 'query_string': b'', 'headers': [(b'x-service-secret', SECRET.encode())],
                 'client': ('127.0.0.1', 1), 'server': ('test', 80)}
        await asyncio.wait_for(app(scope, receive, send), timeout=2)
        assert output[0]['status'] == 408
        body = json.loads(b''.join(part.get('body', b'') for part in output[1:]))
        assert body['code'] == 'RequestTimeout'

    with TestClient(create_app(Settings('postgresql://unused', SECRET))) as client:
        asyncio.run(request(client.app))


def test_mounted_service_keeps_auth_and_body_limits():
    from fastapi import FastAPI

    parent = FastAPI()
    child = create_app(Settings('postgresql://unused', SECRET))
    parent.mount('/rating', child)
    with TestClient(parent) as client:
        assert_safe(client.post('/rating/internal/messages', content=b'{broken'), 401)
        assert_safe(client.post('/rating/internal/messages', content=b' ' * 20000,
                                headers={'X-Service-Secret': SECRET}), 413)
