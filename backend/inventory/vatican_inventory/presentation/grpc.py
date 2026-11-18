"""Read-only gRPC adapter; the same application use cases serve HTTP and RPC."""
from concurrent.futures import ThreadPoolExecutor
import hmac
import json
import logging
from pathlib import Path
from uuid import UUID

import grpc

from ..application.broker import BrokerOperations
from ..application.health import HealthUseCases
from ..application.errors import ApplicationError
from ..application.use_cases import InventoryUseCases
from ..integrations.grpc_contract import inventory_pb2 as pb, inventory_pb2_grpc as rpc
from .errors import classify_error

MAX_RPC_BYTES = 4 * 1024 * 1024


class InventoryReads(rpc.InventoryReadsServicer):
    def __init__(self, inventory, operations, health, secret):
        self.inventory, self.operations, self.health, self.secret = inventory, operations, health, secret.encode()

    def _invoke(self, context, operation, *, authenticated=True):
        if authenticated:
            credentials = [value for key, value in context.invocation_metadata() if key == 'x-service-secret']
            if (len(credentials) != 1 or not isinstance(credentials[0], str)
                    or not hmac.compare_digest(credentials[0].encode(), self.secret)):
                context.abort(grpc.StatusCode.UNAUTHENTICATED, 'Service credential required')
        if not context.is_active():
            context.abort(grpc.StatusCode.DEADLINE_EXCEEDED, 'Request deadline exceeded')
        try:
            return operation()
        except Exception as error:
            spec = classify_error(error)
            status = {401: grpc.StatusCode.UNAUTHENTICATED, 403: grpc.StatusCode.PERMISSION_DENIED,
                      404: grpc.StatusCode.NOT_FOUND, 409: grpc.StatusCode.ALREADY_EXISTS,
                      422: grpc.StatusCode.INVALID_ARGUMENT, 503: grpc.StatusCode.UNAVAILABLE}.get(spec.status, grpc.StatusCode.INTERNAL)
            if spec.code == 'ConcurrentModification':
                status = grpc.StatusCode.ABORTED
            context.set_trailing_metadata((('error-code', spec.code),))
            if spec.status == 500:
                logging.getLogger(__name__).error('Inventory RPC failed exception=%s', type(error).__name__)
            context.abort(status, spec.detail)

    @staticmethod
    def _identity(value):
        try:
            return UUID(value)
        except (ValueError, TypeError, AttributeError):
            raise ApplicationError(422, 'Invalid identifier') from None

    @staticmethod
    def _json(value):
        return pb.JsonReply(json_utf8=json.dumps(value, allow_nan=False, separators=(',', ':')).encode())

    def GetSnapshot(self, request, context):
        return self._invoke(context, lambda: self._json(self.inventory.get(self._identity(request.account_id), selected_only=True)))

    def GetCommandStatus(self, request, context):
        return self._invoke(context, lambda: self._json(self.operations.get(self._identity(request.message_id))))

    def CheckReady(self, request, context):
        return self._invoke(context, lambda: pb.ReadyReply(schema_revision=self.health.ready()['schemaRevision']), authenticated=False)


def create_server(container, settings, bind='0.0.0.0:50051', *, certificate=None, private_key=None):
    server = grpc.server(ThreadPoolExecutor(max_workers=8), maximum_concurrent_rpcs=32,
        options=(('grpc.max_receive_message_length', MAX_RPC_BYTES), ('grpc.max_send_message_length', MAX_RPC_BYTES)))
    rpc.add_InventoryReadsServicer_to_server(InventoryReads(container.get(InventoryUseCases),
        container.get(BrokerOperations), container.get(HealthUseCases), settings.service_secret), server)
    if bool(certificate) != bool(private_key):
        raise ValueError('gRPC TLS requires both certificate and private key')
    if certificate:
        credentials = grpc.ssl_server_credentials(((Path(private_key).read_bytes(), Path(certificate).read_bytes()),))
        port = server.add_secure_port(bind, credentials)
    else:
        port = server.add_insecure_port(bind)
    if port == 0:
        raise RuntimeError('Inventory gRPC could not bind')
    return server, port
