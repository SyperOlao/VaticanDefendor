"""Prepare and run the local Unity Dedicated Server alongside Docker Compose."""
import json
import os
from pathlib import Path
import re
import secrets
import signal
import socket
import subprocess
import time
from uuid import UUID, uuid4

ROOT = Path(__file__).resolve().parents[2]
RUNTIME = ROOT / "backend/.runtime/dedicated-online"
SUPERVISOR = ROOT / "backend/unity_dedicated_server/entrypoint.sh"
PID_FILE = "server.pid"
LAUNCH_FILE = "server-launch.json"
LOG_FILE = "server.log"
DEFAULT_ENERGY_RULESET = "energy-local-v1"


def executable(override):
    binary = ROOT / Path(override).expanduser()
    if not binary.is_file() or not os.access(binary, os.X_OK):
        raise RuntimeError(f"VD_LOCAL_SERVER_EXECUTABLE is not executable: {binary}")
    return binary.resolve()


def content_version():
    selected = (ROOT / "Assets/Resources/NetworkContent.asset").read_text()
    reference = re.search(r"_release:.*guid: ([0-9a-f]{32})", selected)
    if reference:
        for meta in (ROOT / "Assets/ActiveCombat/Content/Releases").glob("*.asset.meta"):
            if "guid: " + reference[1] in meta.read_text():
                release = re.search(r"_releaseId: (sha256-[0-9a-f]{64})", meta.with_suffix("").read_text())
                if release:
                    protocol = (ROOT / "Assets/Scripts/Infrastructure/Networking/Contracts/JoinProtocol.cs").read_text()
                    version = re.search(r"const int Version\s*=\s*(\d+);", protocol)
                    if version:
                        return release[1], version[1]
    raise RuntimeError("Could not read the selected Unity ContentRelease or join protocol.")


def local_settings(environment, port, release):
    """Keep operator-provided slots and policies. Provision only the missing local defaults."""
    slots = json.loads(environment.get("VD_SERVER_POOL_JSON") or "[]")
    rewards = json.loads(environment.get("VD_RUN_REWARDS_JSON") or "{}")
    candidates = [slot for slot in slots if slot["host"] == "127.0.0.1" and int(slot["port"]) == port]
    if len(candidates) > 1 or (candidates and candidates[0].get("transport", "udp-loopback") != "udp-loopback"):
        raise RuntimeError(f"Expected one udp-loopback slot for 127.0.0.1:{port}.")
    changes = {}
    if candidates:
        slot = candidates[0]
    else:
        slot = {"serverId": str(uuid4()), "host": "127.0.0.1", "port": port, "secret": secrets.token_hex(32)}
        slots.append(slot)
        changes["VD_SERVER_POOL_JSON"] = json.dumps(slots, separators=(",", ":"))
    UUID(slot["serverId"])
    if release not in rewards:
        rewards[release] = {"perChurchLevel": 1}
        changes["VD_RUN_REWARDS_JSON"] = json.dumps(rewards, separators=(",", ":"))
    if not environment.get("VD_ENERGY_RULESET_ID"):
        changes["VD_ENERGY_RULESET_ID"] = DEFAULT_ENERGY_RULESET
    return slot, changes


def query(compose, config, sql):
    environment = config["services"]["game_backend"]["environment"]
    return compose("exec", "-T", "postgres", "psql", "-X", "-At", "-v", "ON_ERROR_STOP=1",
                   "-U", environment["VD_DB_USER"], "-d", environment["VD_DB_NAME"], "-c", sql,
                   capture=True).strip()


def seed_local_rules(compose, config, release):
    energy = config["services"]["game_backend"]["environment"]["VD_ENERGY_RULESET_ID"]
    if not re.fullmatch(r"[A-Za-z0-9_.-]{1,128}", energy):
        raise RuntimeError("VD_ENERGY_RULESET_ID is invalid.")
    if not query(compose, config, f"SELECT 1 FROM energy_configs WHERE ruleset_id='{energy}' AND run_cost IS NOT NULL"):
        if energy != DEFAULT_ENERGY_RULESET:
            raise RuntimeError(f"Publish the configured energy ruleset {energy} before starting a match.")
        compose("exec", "-T", "game_backend", "python", "-m", "vatican_backend.cli.configure_energy",
                "--ruleset-id", energy, "--max-energy", "100", "--regen-interval-ms", "60000",
                "--energy-per-tick", "1", "--run-cost", "5")
    if not query(compose, config, f"SELECT 1 FROM progression_configurations WHERE ruleset_id='{release}'"):
        compose("exec", "-T", "game_backend", "python", "-c", """import json
from pathlib import Path
import subprocess
import sys
import tempfile

policy = json.loads(Path('/app/progression-example.json').read_text())
policy['rulesetId'] = sys.argv[1]
with tempfile.TemporaryDirectory() as directory:
    config = Path(directory) / 'progression.json'
    config.write_text(json.dumps(policy))
    subprocess.run([sys.executable, '-m', 'vatican_backend.cli.configure_progression',
                    '--file', str(config)], check=True)
""", release)


def managed_pid():
    path = RUNTIME / PID_FILE
    if not path.exists():
        return None
    pid = int(path.read_text())
    if pid < 2:
        raise RuntimeError("Invalid Dedicated Server PID file.")
    result = subprocess.run(["ps", "-ww", "-p", str(pid), "-o", "pgid=", "-o", "args="],
                            capture_output=True, text=True, check=False).stdout.strip()
    fields = result.split(maxsplit=1)
    if len(fields) == 2 and fields[0] == str(pid) and fields[1].startswith(f"/bin/sh {SUPERVISOR} "):
        return pid
    return None


def registration(compose, config, slot):
    server_id = str(UUID(slot["serverId"]))
    row = query(compose, config, "SELECT status, content_release_id, protocol_version, "
                "last_seen, last_seen > now() - interval '15 seconds' FROM game_servers "
                f"WHERE id='{server_id}' AND host='127.0.0.1' AND port={int(slot['port'])}")
    return row.split("|") if row else None


def is_registered(row, release, protocol):
    return bool(row and row[0] in ("ready", "assigned", "occupied") and
                row[1:3] == [release, protocol] and row[4] == "t")


def running_command(pid):
    return subprocess.run(["ps", "-ww", "-p", str(pid), "-o", "args="],
                          capture_output=True, text=True, check=False).stdout.strip()


def replace_if_idle(compose, config, slot, pid, binary):
    server_id = str(UUID(slot["serverId"]))
    command = running_command(pid)
    if not command:
        return
    match = re.search(r" -vdServerId ([0-9A-Fa-f-]+)(?: |$)", command)
    if not match or UUID(match[1]) != UUID(server_id):
        raise RuntimeError("The running Dedicated Server uses a different slot. Stop it before changing the port.")
    path = RUNTIME / LAUNCH_FILE
    if path.is_file() and json.loads(path.read_text()) == {"pid": pid, "binary": str(binary)}:
        return
    # Stop admitting matches atomically with the check for an active assignment.
    state = query(compose, config, f"""WITH parked AS (
        UPDATE game_servers SET status='unavailable' WHERE id='{server_id}'
        AND status='ready' AND current_run_id IS NULL RETURNING id
    )
    SELECT CASE WHEN EXISTS (SELECT 1 FROM parked) THEN 'idle'
                WHEN EXISTS (SELECT 1 FROM game_servers WHERE id='{server_id}') THEN 'busy'
                ELSE 'missing' END""")
    if state == "busy":
        raise RuntimeError("The updated build is available. The current server has a match or is changing state. "
                           "Run up.sh again after the slot is ready.")
    stop()


def start(compose, config, slot, release, protocol, binary):
    existing = managed_pid()
    if existing:
        replace_if_idle(compose, config, slot, existing, binary)
        existing = managed_pid()
    previous = registration(compose, config, slot)
    if existing and is_registered(previous, release, protocol):
        print(f"Dedicated Server already registered on 127.0.0.1:{slot['port']}/udp (PID {existing}).")
        return

    process = None
    if not existing:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as probe:
            try:
                probe.bind(("127.0.0.1", int(slot["port"])))
            except OSError as error:
                raise RuntimeError(f"UDP port {slot['port']} is already in use.") from error
        gateway_port = config["services"]["gateway"]["ports"][0]["published"]
        environment = dict(os.environ, VD_SERVER_EXECUTABLE=str(binary),
                           VD_BACKEND_URL=f"http://127.0.0.1:{gateway_port}", VD_INSECURE_LOCAL_BACKEND="true",
                           VD_SERVER_SECRET=slot["secret"], VD_CONTENT_RELEASE_ID="",
                           VD_RUN_RESULT_DIR=str(RUNTIME / "pending-results"))
        with os.fdopen(os.open(RUNTIME / LOG_FILE, os.O_WRONLY | os.O_APPEND | os.O_CREAT, 0o600), "a") as log:
            process = subprocess.Popen(["/bin/sh", str(SUPERVISOR), "-vdBind", "127.0.0.1",
                                        "-vdPort", str(slot["port"]), "-vdServerId", slot["serverId"]],
                                       env=environment, stdin=subprocess.DEVNULL, stdout=log,
                                       stderr=subprocess.STDOUT, start_new_session=True)
        (RUNTIME / PID_FILE).write_text(f"{process.pid}\n")
        (RUNTIME / LAUNCH_FILE).write_text(json.dumps({"pid": process.pid, "binary": str(binary)}))
    try:
        deadline = time.monotonic() + 45
        while time.monotonic() < deadline:
            if process and process.poll() is not None:
                break
            row = registration(compose, config, slot)
            new_registration = row and (not previous or row[3] != previous[3])
            if is_registered(row, release, protocol) and (existing or new_registration):
                print(f"Dedicated Server registered on 127.0.0.1:{slot['port']}/udp (protocol {protocol}).")
                return
            if new_registration and not existing and row[1:3] != [release, protocol]:
                break
            time.sleep(1)
        raise RuntimeError("Dedicated Server did not register with the selected content and protocol. "
                           f"Check the build version and {RUNTIME / LOG_FILE}.")
    except BaseException:
        if process and process.poll() is None:
            stop(process)
        raise


def stop(process=None):
    pid = process.pid if process is not None else managed_pid()
    if pid is None:
        return
    # The launcher owns this session. Give the supervisor time to request Unity shutdown.
    try:
        os.kill(pid, signal.SIGTERM)
        for timeout, fallback in ((10, signal.SIGTERM), (3, signal.SIGKILL), (2, None)):
            deadline = time.monotonic() + timeout
            while time.monotonic() < deadline:
                if process is not None:
                    process.poll()
                os.killpg(pid, 0)
                time.sleep(.2)
            if fallback is None:
                raise RuntimeError(f"Dedicated Server process group {pid} is still running.")
            os.killpg(pid, fallback)
    except ProcessLookupError:
        pass
