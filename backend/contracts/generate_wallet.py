"""Generate identical protocol-only stubs inside each independently built service."""
from pathlib import Path
import subprocess
import sys
import tempfile

root = Path(__file__).resolve().parent.parent
source = root / 'contracts/wallet/v1'
with tempfile.TemporaryDirectory() as temporary:
    subprocess.run([sys.executable, '-m', 'grpc_tools.protoc', '-I' + str(source),
                    '--python_out=' + temporary, '--grpc_python_out=' + temporary,
                    str(source / 'wallet.proto')], check=True)
    for service, package in [('wallet', 'vatican_wallet'), ('game_backend', 'vatican_backend'), ('shop', 'vatican_shop')]:
        target = root / service / package / 'integrations/wallet_contract'
        target.mkdir(parents=True, exist_ok=True)
        (target / '__init__.py').write_text('"""Generated wallet protocol; no service implementation imports."""\n')
        for name in ('wallet_pb2.py', 'wallet_pb2_grpc.py'):
            value = (Path(temporary) / name).read_text()
            value = value.replace('import wallet_pb2 as wallet__pb2', 'from . import wallet_pb2 as wallet__pb2')
            (target / name).write_text(value)
