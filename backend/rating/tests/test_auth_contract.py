from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager
from uuid import UUID, uuid4

import grpc
import pytest

from vatican_rating.application.errors import ApplicationError
from vatican_rating.bootstrap.settings import Settings
from vatican_rating.integrations.auth_grpc import AuthGrpcGateway
from vatican_rating.integrations.auth_contract import auth_pb2, auth_pb2_grpc

SECRET = 'grpc-contract-test-' + 's' * 32


@contextmanager
def endpoint(callback, port=0):
    executor = ThreadPoolExecutor(max_workers=16)
    server = grpc.server(executor, options=(('grpc.so_reuseport', 0),))

    class Service(auth_pb2_grpc.AuthServiceServicer):
        def Authenticate(self, request, context):
            assert dict(context.invocation_metadata())['x-service-secret'] == SECRET
            return callback(request, context)

    auth_pb2_grpc.add_AuthServiceServicer_to_server(Service(), server)
    port = server.add_insecure_port(f'127.0.0.1:{port}')
    server.start()
    try:
        yield server, port
    finally:
        server.stop(0).wait(3)
        executor.shutdown(wait=True)


def gateway(port, **options):
    return AuthGrpcGateway(Settings('postgresql://unused', SECRET,
        auth_grpc_target=f'127.0.0.1:{port}', **options))


@pytest.mark.parametrize('code,status,attempts', [
    (grpc.StatusCode.UNAUTHENTICATED, 401, 1),
    (grpc.StatusCode.PERMISSION_DENIED, 503, 1),
    (grpc.StatusCode.UNAVAILABLE, 503, 3),
    (grpc.StatusCode.INTERNAL, 503, 1),
    (grpc.StatusCode.DEADLINE_EXCEEDED, 503, 1),
    (grpc.StatusCode.RESOURCE_EXHAUSTED, 503, 1),
    (grpc.StatusCode.UNIMPLEMENTED, 503, 1),
])
def test_error_mapping_retry_budget_and_private_diagnostics(code, status, attempts):
    calls = []

    def fail(request, context):
        calls.append(1)
        context.abort(code, 'PRIVATE_TOKEN SQL password=not-public')

    with endpoint(fail) as (_, port):
        client = gateway(port)
        try:
            with pytest.raises(ApplicationError) as error:
                client.authenticate('opaque')
            assert error.value.status_code == status
            assert 'PRIVATE' not in error.value.detail and 'password' not in error.value.detail
            assert len(calls) == attempts
        finally:
            client.close()


def test_temporary_unavailable_is_retried_but_successful_identity_is_not_cached():
    account, calls = uuid4(), []

    def reply(request, context):
        calls.append(1)
        if len(calls) <= 2:
            context.abort(grpc.StatusCode.UNAVAILABLE, 'temporary')
        if len(calls) > 3:
            context.abort(grpc.StatusCode.UNAUTHENTICATED, 'revoked')
        return auth_pb2.AuthenticateReply(account_id=str(account), is_guest=True)

    with endpoint(reply) as (_, port):
        client = gateway(port)
        try:
            assert client.authenticate('opaque').account_id == account
            assert len(calls) == 3
            with pytest.raises(ApplicationError) as error:
                client.authenticate('opaque')
            assert error.value.status_code == 401 and len(calls) == 4
        finally:
            client.close()


@pytest.mark.parametrize('reply', [
    auth_pb2.AuthenticateReply(account_id='invalid', is_guest=True),
    auth_pb2.AuthenticateReply(account_id=str(UUID(int=0)), is_guest=False),
    auth_pb2.AuthenticateReply(account_id=str(uuid4())),
])
def test_incomplete_or_invalid_response_never_becomes_a_principal(reply):
    with endpoint(lambda request, context: reply) as (_, port):
        client = gateway(port)
        try:
            with pytest.raises(ApplicationError) as error:
                client.authenticate('opaque')
            assert error.value.status_code == 503
        finally:
            client.close()


def test_parallel_requests_share_channel_without_mixing_identities():
    def reply(request, context):
        account = dict(context.invocation_metadata())['authorization'].split(' ', 1)[1]
        return auth_pb2.AuthenticateReply(account_id=account, is_guest=True)

    with endpoint(reply) as (_, port):
        client = gateway(port)
        try:
            accounts = [uuid4() for _ in range(40)]
            with ThreadPoolExecutor(max_workers=8) as pool:
                results = list(pool.map(lambda account: client.authenticate(str(account)).account_id, accounts))
            assert results == accounts
        finally:
            client.close()
