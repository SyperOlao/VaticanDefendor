"""Internal wallet reads, with a bounded deadline and no HTTP fallback."""
import json
from pathlib import Path
import grpc
from ..application.errors import ApplicationError
from .wallet_contract import wallet_pb2 as pb, wallet_pb2_grpc as rpc


class WalletGrpcClient:
    def __init__(self, target, secret, ca_file=None, timeout=5):
        if not target or '://' in target or not 0 < timeout <= 10:
            raise ValueError('Invalid wallet gRPC target/deadline')
        options = (('grpc.max_receive_message_length', 4 * 1024 * 1024), ('grpc.enable_retries', 0))
        self.channel = (grpc.secure_channel(target, grpc.ssl_channel_credentials(Path(ca_file).read_bytes()), options=options)
                        if ca_file else grpc.insecure_channel(target, options=options))
        self.stub, self.secret, self.timeout = rpc.WalletReadsStub(self.channel), secret, timeout

    def _read(self, method, request, *, missing=False):
        try:
            response = method(request, timeout=self.timeout, metadata=(('x-service-secret', self.secret),))
        except grpc.RpcError as error:
            if missing and error.code() == grpc.StatusCode.NOT_FOUND:
                return None
            raise ApplicationError(503, 'Wallet gRPC is unavailable') from None
        try:
            value = json.loads(response.json_utf8, parse_constant=lambda _: (_ for _ in ()).throw(ValueError()))
            if not isinstance(value, dict):
                raise ValueError()
            return value
        except (ValueError, TypeError, RecursionError):
            raise ApplicationError(503, 'Invalid wallet gRPC response') from None

    def command_status(self, message_id):
        return self._read(self.stub.GetCommandStatus, pb.CommandRequest(message_id=str(message_id)), missing=True)

    def close(self):
        self.channel.close()
