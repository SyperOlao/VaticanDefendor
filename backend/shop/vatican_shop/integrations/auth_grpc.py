"""Thread-safe auth channel; bounded retries only for read-only introspection."""
from pathlib import Path
import random
import time
from uuid import UUID

import grpc

from ..application.errors import ApplicationError
from ..application.models import Principal
from .auth_contract import auth_pb2, auth_pb2_grpc


class AuthGrpcGateway:
    def __init__(self, settings):
        self.secret = settings.service_secret
        self.timeout = settings.auth_grpc_timeout_ms / 1000
        self.attempts = settings.auth_grpc_attempts
        options = (('grpc.enable_retries', 0),  # One explicit retry budget, no nested retry policies.
                   ('grpc.initial_reconnect_backoff_ms', 100),
                   ('grpc.min_reconnect_backoff_ms', 100),
                   ('grpc.max_reconnect_backoff_ms', 1000),
                   ('grpc.max_receive_message_length', 65536),
                   ('grpc.max_send_message_length', 4096))
        if settings.auth_grpc_ca_file:
            credentials = grpc.ssl_channel_credentials(Path(settings.auth_grpc_ca_file).read_bytes())
            self.channel = grpc.secure_channel(settings.auth_grpc_target, credentials, options=options)
        else:
            self.channel = grpc.insecure_channel(settings.auth_grpc_target, options=options)
        self.stub = auth_pb2_grpc.AuthServiceStub(self.channel)

    def close(self):
        self.channel.close()

    def authenticate(self, token):
        if (not isinstance(token, str) or not token or len(token) > 512 or not token.isascii()
                or not token.isprintable() or any(c.isspace() for c in token)):
            raise ApplicationError(401, 'Bearer credential required')
        deadline = time.monotonic() + self.timeout
        for attempt in range(self.attempts):
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                break
            try:
                response = self.stub.Authenticate(auth_pb2.AuthenticateRequest(), timeout=remaining,
                    metadata=(('authorization', 'Bearer ' + token), ('x-service-secret', self.secret)),
                    wait_for_ready=False)
            except grpc.RpcError as error:
                if error.code() == grpc.StatusCode.UNAUTHENTICATED:
                    raise ApplicationError(401, 'Credential is invalid or expired') from None
                # PermissionDenied means the *service* credential failed, not the player's token.
                if error.code() != grpc.StatusCode.UNAVAILABLE or attempt + 1 == self.attempts:
                    break
                pause = min(random.uniform(.04, .06) * (2 ** attempt), max(0, deadline - time.monotonic()))
                if pause:
                    time.sleep(pause)
                continue
            except ValueError:
                # Closed channel during shutdown. No runtime/metadata diagnostics escape to HTTP.
                break
            try:
                account = UUID(response.account_id)
                if account.int == 0 or not response.HasField('is_guest'):
                    raise ValueError()
                return Principal(account, response.is_guest, tuple(response.roles))
            except (ValueError, TypeError, AttributeError):
                raise ApplicationError(503, 'Auth returned an invalid response') from None
        raise ApplicationError(503, 'Auth service is unavailable')
