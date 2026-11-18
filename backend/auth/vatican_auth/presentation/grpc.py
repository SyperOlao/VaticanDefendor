"""Internal gRPC listener shares auth's event loop, DI and authorization policy."""
import hmac
import logging
from pathlib import Path

import grpc
from sqlalchemy.exc import SQLAlchemyError

from ..application.access import AuthorizeUseCase
from ..application.errors import InvalidCredentials
from ..integrations.auth_contract import auth_pb2, auth_pb2_grpc

logger = logging.getLogger('vatican_auth.grpc')


class AuthService(auth_pb2_grpc.AuthServiceServicer):
    def __init__(self, container, service_secret):
        self.container, self.service_secret = container, service_secret

    async def Authenticate(self, request, context):
        metadata = context.invocation_metadata()
        secrets = [value for key, value in metadata if key == 'x-service-secret']
        if (len(secrets) != 1 or not isinstance(secrets[0], str) or not secrets[0].isascii()
                or len(secrets[0]) != len(self.service_secret)
                or not hmac.compare_digest(self.service_secret, secrets[0])):
            # A misconfigured caller is not an expired player session. Rating maps this to 503.
            await context.abort(grpc.StatusCode.PERMISSION_DENIED, 'Service credential required')
        authorizations = [value for key, value in metadata if key == 'authorization']
        scheme, _, token = (authorizations[0] if len(authorizations) == 1 else '').partition(' ')
        if (scheme.lower() != 'bearer' or not token or len(token) > 512 or not token.isascii()
                or not token.isprintable() or any(c.isspace() for c in token)):
            await context.abort(grpc.StatusCode.UNAUTHENTICATED, 'Credential is invalid or expired')
        try:
            async with self.container() as scope:
                use_case = await scope.get(AuthorizeUseCase)
                access = await use_case.execute(token)
            reply = auth_pb2.AuthenticateReply(account_id=str(access.account_id), is_guest=access.is_guest,
                roles=access.roles, permissions=access.permissions)
            if access.session_id is not None:
                reply.session_id = str(access.session_id)
            return reply
        except InvalidCredentials:
            await context.abort(grpc.StatusCode.UNAUTHENTICATED, 'Credential is invalid or expired')
        except SQLAlchemyError:
            logger.warning('Auth gRPC database unavailable')
            await context.abort(grpc.StatusCode.UNAVAILABLE, 'Auth service is unavailable')
        except Exception as error:
            # Exception text/tracebacks may contain SQL or credentials.
            logger.error('Auth gRPC handler failed exception=%s', type(error).__name__)
            await context.abort(grpc.StatusCode.INTERNAL, 'Auth service is unavailable')


async def start_auth_server(container, settings):
    server = grpc.aio.server(options=(('grpc.max_receive_message_length', 4096),
                                     ('grpc.max_send_message_length', 65536),
                                     ('grpc.so_reuseport', 0)), maximum_concurrent_rpcs=100)
    auth_pb2_grpc.add_AuthServiceServicer_to_server(AuthService(container, settings.service_secret), server)
    try:
        if settings.certificate_file:
            credentials = grpc.ssl_server_credentials(((Path(settings.private_key_file).read_bytes(),
                                                        Path(settings.certificate_file).read_bytes()),))
            port = server.add_secure_port(settings.bind, credentials)
        else:
            port = server.add_insecure_port(settings.bind)
        if not port:
            raise RuntimeError('Auth gRPC could not bind listener')
        await server.start()
    except BaseException:
        await server.stop(0)
        raise
    return server, port
