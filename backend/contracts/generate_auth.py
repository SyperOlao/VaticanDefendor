"""Generate the same versioned auth protocol in independently built services."""
from pathlib import Path
import subprocess
import sys
import tempfile

root = Path(__file__).resolve().parent.parent
source = root / 'contracts/auth/v1'
with tempfile.TemporaryDirectory() as temporary:
    subprocess.run([sys.executable, '-m', 'grpc_tools.protoc', '-I' + str(source),
                    '--python_out=' + temporary, '--grpc_python_out=' + temporary,
                    str(source / 'auth.proto')], check=True)
    for service, package in [('auth', 'vatican_auth'), ('rating', 'vatican_rating'), ('game_backend', 'vatican_backend'), ('shop', 'vatican_shop'), ('wallet', 'vatican_wallet'), ('inventory', 'vatican_inventory')]:
        target = root / service / package / 'integrations/auth_contract'
        target.mkdir(parents=True, exist_ok=True)
        (target / '__init__.py').write_text('"""Generated auth v1 protocol; no cross-service implementation imports."""\n')
        for name in ('auth_pb2.py', 'auth_pb2_grpc.py'):
            value = (Path(temporary) / name).read_text()
            value = value.replace('import auth_pb2 as auth__pb2', 'from . import auth_pb2 as auth__pb2')
            (target / name).write_text(value)
