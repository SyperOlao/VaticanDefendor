"""Start and verify the entire local backend using only Python and Docker Compose."""
import argparse
import json
import os
from pathlib import Path
import secrets
import subprocess
import sys
import urllib.request

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
        fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
        with os.fdopen(fd, "w") as stream:
            stream.write("\n".join(lines) + "\n")
    path.chmod(0o600)


def compose(*args, capture=False):
    return subprocess.run(
        ["docker", "compose", "--project-directory", str(INFRA),
         "--env-file", str(INFRA / ".env"), "-f", str(INFRA / "compose.yaml"), *args],
        check=True, text=True, stdout=subprocess.PIPE if capture else None,
    ).stdout


def verify(*, require_initializers=False):
    config = json.loads(compose("config", "--format", "json", capture=True))
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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("up", "check", "status", "down", "logs"), nargs="?", default="up")
    args = parser.parse_args()
    configure()
    if args.action == "up":
        compose("up", "-d", "--build", "--wait", "--wait-timeout", "600")
        verify(require_initializers=True)
        config = json.loads(compose("config", "--format", "json", capture=True))
        initializers = [name for name, service in config["services"].items() if service.get("restart") == "no"]
        compose("rm", "--force", *initializers)
        print("Removed successful temporary containers; only running services remain.", flush=True)
    elif args.action == "check":
        verify()
    elif args.action == "status":
        compose("ps", "--all")
    elif args.action == "down":
        compose("down")  # Keep all persistent volumes.
    else:
        compose("logs", "--follow", "--tail", "100")


if __name__ == "__main__":
    try:
        main()
    except (subprocess.CalledProcessError, OSError, ValueError, RuntimeError) as error:
        print(f"Stack command failed: {error}", file=sys.stderr)
        sys.exit(1)
