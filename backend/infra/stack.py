"""Start and verify the entire local backend and optionally its Unity Dedicated Server."""
import argparse
import fcntl
import json
import os
from pathlib import Path
import secrets
import subprocess
import sys
import tempfile
import urllib.request

import dedicated
import server_build

INFRA = Path(__file__).resolve().parent
SECRET_KEYS = {
    "VD_AUTH_DB_PASSWORD", "VD_POSTGRES_PASSWORD", "VD_SERVICE_SECRET",
    "VD_WALLET_DB_PASSWORD", "VD_INVENTORY_DB_PASSWORD", "VD_SHOP_DB_PASSWORD",
    "VD_RATING_DB_PASSWORD", "VD_ANALYTICS_INBOX_DB_PASSWORD", "VD_CLICKHOUSE_PASSWORD",
    "VD_RABBITMQ_PASSWORD", "VD_SHOP_S3_SECRET_KEY", "VD_SUPERSET_SECRET_KEY",
    "VD_SUPERSET_METADATA_DB_PASSWORD", "VD_SUPERSET_ADMIN_PASSWORD",
    "VD_SUPERSET_CLICKHOUSE_PASSWORD",
}


def configure():
    path = INFRA / ".env"
    fresh = not path.exists()
    lines = (INFRA / ".env.example").read_text().splitlines() if fresh else path.read_text().splitlines()
    present = set()
    for index, line in enumerate(lines):
        if "=" not in line or line.lstrip().startswith("#"):
            continue
        key, value = line.split("=", 1)
        present.add(key)
        if key in SECRET_KEYS and (not value.strip(" '\"") or fresh and "replace-with-" in value):
            lines[index] = key + "=" + secrets.token_urlsafe(48)
    for key in sorted(SECRET_KEYS - present):
        lines.append(key + "=" + secrets.token_urlsafe(48))
    # Never replace existing credentials or settings during a restart.
    if fresh or path.read_text().splitlines() != lines:
        write_config(path, lines)
    path.chmod(0o600)


def write_config(path, lines):
    with tempfile.NamedTemporaryFile("w", dir=path.parent, delete=False) as stream:
        stream.write("\n".join(lines) + "\n")
        temporary = Path(stream.name)
    temporary.replace(path)


def update_local_settings(changes):
    if not changes:
        return
    overridden = changes.keys() & os.environ.keys()
    if overridden:
        raise RuntimeError("The shell overrides settings needed for local startup: " + ", ".join(sorted(overridden)))
    path = INFRA / ".env"
    lines = path.read_text().splitlines()
    remaining = dict(changes)
    for i, line in enumerate(lines):
        key = line.partition("=")[0].strip()
        if key in remaining:
            lines[i] = f"{key}='{remaining.pop(key)}'"
    lines.extend(f"{key}='{value}'" for key, value in remaining.items())
    write_config(path, lines)


def local_option(name, default=None):
    if name in os.environ:
        return os.environ[name]
    for line in reversed((INFRA / ".env").read_text().splitlines()):
        key, separator, value = line.partition("=")
        if separator and key.strip() == name:
            return value.strip().strip(" '\"")
    return default


def compose(*args, capture=False):
    return subprocess.run(
        ["docker", "compose", "--project-directory", str(INFRA),
         "--env-file", str(INFRA / ".env"), "-f", str(INFRA / "compose.yaml"), *args],
        check=True, text=True, stdout=subprocess.PIPE if capture else None,
    ).stdout


def compose_config():
    return json.loads(compose("config", "--format", "json", capture=True))


def verify(config, *, require_initializers=False):
    ids = compose("ps", "--all", "--quiet", capture=True).split()
    if not ids:
        raise RuntimeError("No stack containers found")
    containers = json.loads(subprocess.check_output(["docker", "inspect", *ids], text=True))
    states = {c["Config"]["Labels"]["com.docker.compose.service"]: c["State"] for c in containers}
    errors = []
    for name, service in config["services"].items():
        state = states.get(name, {})
        oneshot = service.get("restart") == "no"
        # Successful startup removes temporary containers; check accepts their absence.
        if oneshot and not state and not require_initializers:
            continue
        good = (state.get("Status") == "exited" and state.get("ExitCode") == 0) if oneshot else (
            state.get("Status") == "running" and state.get("Health", {}).get("Status", "healthy") == "healthy"
        )
        if not good:
            errors.append(f"{name}: {state.get('Status', 'missing')} {state.get('Health', {}).get('Status', '')}")
    if errors:
        raise RuntimeError("Stack verification failed:\n" + "\n".join(errors))
    for name in ("auth", "game_backend", "wallet", "inventory", "shop", "rating", "analytics", "gateway", "superset", "shop_s3"):
        service = config["services"][name]
        port = service["ports"][0]["published"]
        path = "/health" if name == "superset" else "/minio/health/live" if name == "shop_s3" else "/health/ready"
        with urllib.request.urlopen(f"http://127.0.0.1:{port}{path}", timeout=10) as response:
            if response.status != 200:
                raise RuntimeError(f"{name} HTTP readiness failed: {response.status}")
        print(f"OK {name}: http://127.0.0.1:{port}", flush=True)
    gateway_port = config["services"]["gateway"]["ports"][0]["published"]
    running = sum(state.get("Status") == "running" for state in states.values())
    print(f"Verified {running} running services.")
    completed = sum(state.get("Status") == "exited" and state.get("ExitCode") == 0 for state in states.values())
    if completed:
        print(f"Verified {completed} successful init/migration containers.")
    print(f"Unity Backend URL: http://127.0.0.1:{gateway_port}")


def local_server(config):
    port = int(local_option("VD_LOCAL_SERVER_PORT", "7979"))
    if not 1 <= port <= 65535:
        raise ValueError("VD_LOCAL_SERVER_PORT must be between 1 and 65535.")
    release, protocol = dedicated.content_version()
    game_environment = config["services"]["game_backend"]["environment"]
    slot, changes = dedicated.local_settings(game_environment, port, release)
    return slot, changes, release, protocol


def up(backend_only):
    config = compose_config()
    if not backend_only:
        override = local_option("VD_LOCAL_SERVER_EXECUTABLE")
        binary = (dedicated.executable(override) if override else
                  server_build.prepare(dedicated.ROOT, dedicated.RUNTIME, local_option("VD_UNITY_EDITOR")))
        slot, changes, release, protocol = local_server(config)
        if changes:
            update_local_settings(changes)
            config = compose_config()

    compose("up", "-d", "--build", "--wait", "--wait-timeout", "600")
    verify(config, require_initializers=True)
    initializers = [name for name, service in config["services"].items() if service.get("restart") == "no"]
    compose("rm", "--force", *initializers)
    print("Removed successful temporary containers; only running services remain.", flush=True)

    if not backend_only:
        dedicated.seed_local_rules(compose, config, release)
        dedicated.start(compose, config, slot, release, protocol, binary)
        path = dedicated.ROOT / ".env"
        gateway_port = config["services"]["gateway"]["ports"][0]["published"]
        try:
            fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        except FileExistsError:
            pass
        else:
            with os.fdopen(fd, "w") as stream:
                stream.write(f"VD_BACKEND_URL=http://127.0.0.1:{gateway_port}\nVD_INSECURE_LOCAL_BACKEND=true\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("up", "check", "status", "down", "logs"), nargs="?", default="up")
    parser.add_argument("--backend-only", action="store_true", help="Skip the local Unity Dedicated Server.")
    args = parser.parse_args()
    if args.action in ("up", "down"):
        dedicated.RUNTIME.mkdir(mode=0o700, parents=True, exist_ok=True)
        with (dedicated.RUNTIME / "stack.lock").open("a") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX)
            configure()
            if args.action == "up":
                up(args.backend_only)
            else:
                if not args.backend_only:
                    dedicated.stop()
                compose("down")  # Keep all persistent volumes.
        return

    if not (INFRA / ".env").is_file():
        raise RuntimeError("Local configuration is missing. Start the stack with backend/infra/up.sh.")
    if args.action == "check":
        config = compose_config()
        verify(config)
        if not args.backend_only:
            slot, changes, release, protocol = local_server(config)
            row = dedicated.registration(compose, config, slot) if not changes else None
            if not dedicated.managed_pid() or not dedicated.is_registered(row, release, protocol):
                raise RuntimeError("Local Dedicated Server is not registered with the selected release and protocol.")
            print(f"Dedicated Server on 127.0.0.1:{slot['port']}/udp: {row[0]}, protocol {protocol}.")
    elif args.action == "status":
        compose("ps", "--all")
        if not args.backend_only:
            pid = dedicated.managed_pid()
            print(f"Dedicated Server PID: {pid}" if pid else "Dedicated Server stopped.")
    else:
        compose("logs", "--follow", "--tail", "100")


if __name__ == "__main__":
    try:
        main()
    except (subprocess.CalledProcessError, OSError, ValueError, RuntimeError) as error:
        print(f"Stack command failed: {error}", file=sys.stderr)
        sys.exit(1)
