import os
from pathlib import Path
import grpc
from ..integrations.grpc_contract import inventory_pb2 as pb, inventory_pb2_grpc as rpc


def main():
    target = os.getenv('VD_INVENTORY_GRPC_HEALTH_TARGET', '127.0.0.1:50051')
    ca = os.getenv('VD_INVENTORY_GRPC_CA_FILE')
    channel = grpc.secure_channel(target, grpc.ssl_channel_credentials(Path(ca).read_bytes())) if ca else grpc.insecure_channel(target)
    try:
        rpc.InventoryReadsStub(channel).CheckReady(pb.ReadyRequest(), timeout=2)
    except grpc.RpcError:
        raise SystemExit(1) from None
    finally:
        channel.close()


if __name__ == '__main__':
    main()
