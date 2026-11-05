"""Local macOS launcher. No packages beyond Python 3's standard library required."""
import argparse
import json
import os
from pathlib import Path
import plistlib
import re
import secrets
import shutil
import subprocess
import sys
import time
import urllib.request
from uuid import uuid4

ROOT = Path(__file__).resolve().parents[2]
BACKEND = ROOT / "backend"
RUNTIME = BACKEND / ".runtime"
LOGS = ROOT / "Builds/OnlineMac"


def release_id():
    selected = (ROOT / "Assets/Resources/NetworkContent.asset").read_text()
    match = re.search(r"_release:.*guid: ([0-9a-f]{32})", selected)
    if not match:
        raise RuntimeError("NetworkContent.asset has no selected ContentRelease")
    for meta in (ROOT / "Assets/ActiveCombat/Content/Releases").glob("*.asset.meta"):
        if "guid: " + match[1] in meta.read_text():
            content = meta.with_suffix("").read_text()
            value = re.search(r"_releaseId: (sha256-[0-9a-f]{64})", content)
            if value:
                return value[1]
    raise RuntimeError("Selected ContentRelease was not found")


def config(create=False):
    path = RUNTIME / "local.json"
    if not path.exists():
        if not create:
            raise RuntimeError("First run: python3 backend/scripts/local.py backend --reward-per-church-level 1")
        RUNTIME.mkdir(parents=True, exist_ok=True)
        data = dict(serverId=str(uuid4()), host="127.0.0.1", port=7979,
                    dbPassword=secrets.token_hex(24), serverSecret=secrets.token_hex(32))
        with path.open("x") as stream:
            os.chmod(path, 0o600)
            json.dump(data, stream, indent=2)
    return json.loads(path.read_text(encoding="utf-8-sig"))


def compose(*args):
    subprocess.run(["docker", "compose", "--env-file", str(RUNTIME / "compose.env"),
                    "-f", str(BACKEND / "compose.yaml"), *args], cwd=ROOT, check=True)


def backend(reward):
    if not shutil.which("docker") or subprocess.run(["docker", "info"], stdout=subprocess.DEVNULL,
                                                    stderr=subprocess.DEVNULL).returncode:
        raise RuntimeError("Start Docker Desktop and wait until its engine is running.")
    data = config(create=True)
    slot = {key: data[key] for key in ("serverId", "host", "port")}
    slot["secret"] = data["serverSecret"]
    policy = {release_id(): {"perChurchLevel": reward}}
    envfile = RUNTIME / "compose.env"
    envfile.touch(mode=0o600, exist_ok=True)
    os.chmod(envfile, 0o600)
    envfile.write_text("VD_DB_PASSWORD=" + data["dbPassword"] + "\n" +
                       "VD_SERVER_POOL_JSON='" + json.dumps([slot]) + "'\n" +
                       "VD_RUN_REWARDS_JSON='" + json.dumps(policy) + "'\n")
    compose("up", "-d", "--build")
    for _ in range(45):
        try:
            with urllib.request.urlopen("http://127.0.0.1:8080/health/ready", timeout=2) as response:
                if json.load(response)["status"] == "ready":
                    print("Backend ready: http://127.0.0.1:8080. Now run local.py server, then local.py client.")
                    return
        except (OSError, ValueError):
            pass
        time.sleep(2)
    raise RuntimeError("Backend did not become ready. Run local.py logs.")


def build(role):
    version = re.search(r"m_EditorVersion: (\S+)", (ROOT / "ProjectSettings/ProjectVersion.txt").read_text())[1]
    editor = Path("/Applications/Unity/Hub/Editor") / version / "Unity.app/Contents/MacOS/Unity"
    if not editor.exists():
        raise RuntimeError("Unity Editor not found: " + str(editor))
    LOGS.mkdir(parents=True, exist_ok=True)
    log = LOGS / (role.lower() + "-build.log")
    subprocess.run([str(editor), "-batchmode", "-nographics", "-quit", "-projectPath", str(ROOT),
                    "-executeMethod", "VaticanDefendor.Editor.NetworkPrototypeBuild.BuildMac" + role,
                    "-logFile", str(log)], check=True)
    print("Build completed. Log:", log)


def build_executable(role):
    app = LOGS / role / "VaticanDefendor.app"
    info = app / "Contents/Info.plist"
    if info.exists():
        with info.open("rb") as stream:
            executable = app / "Contents/MacOS" / plistlib.load(stream)["CFBundleExecutable"]
    elif role == "Server" and (app / "Data/app.info").exists():
        # Unity's Mac Dedicated Server is a flat executable + Data directory,
        # not a graphical .app bundle, even when the output name ends in .app.
        lines = (app / "Data/app.info").read_text().splitlines()
        if len(lines) < 2 or Path(lines[1]).name != lines[1]:
            raise RuntimeError("Invalid dedicated server Data/app.info")
        executable = app / lines[1]
    else:
        return None
    return executable if executable.is_file() and os.access(executable, os.X_OK) else None


def launch(role):
    executable = build_executable(role)
    if executable is None:
        raise RuntimeError("Build is missing. Run local.py build-" + role.lower())
    env = os.environ.copy()
    # This helper deliberately runs the local bundled-content setup.
    for name in ("VD_CONTENT_RELEASE_ID", "VD_GUEST_TOKEN", "VD_LOGIN", "VD_PASSWORD"):
        env.pop(name, None)
    args = [str(executable), "-vdRole", role.lower(), "-vdBackendUrl", "http://127.0.0.1:8080",
            "-vdInsecureLocalBackend", "-logFile", str(LOGS / (role.lower() + ".log"))]
    if role == "Server":
        data = config()
        env["VD_SERVER_SECRET"] = data["serverSecret"]
        args += ["-batchmode", "-nographics", "-vdServerId", data["serverId"], "-vdPort", str(data["port"])]
    print("Starting", role, "— keep this terminal open. Log:", LOGS / (role.lower() + ".log"), flush=True)
    os.chdir(ROOT)
    os.execve(str(executable), args, env)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["backend", "server", "client", "build-server", "build-client", "stop", "logs", "status"])
    parser.add_argument("--reward-per-church-level", type=int, help="Required for backend; 1 is suitable only as a local test value")
    args = parser.parse_args()
    if args.command == "backend":
        if args.reward_per_church_level is None or not 1 <= args.reward_per_church_level <= 1_000_000:
            parser.error("backend requires --reward-per-church-level within 1..1000000")
        backend(args.reward_per_church_level)
    elif args.command in ("server", "client"):
        launch(args.command.title())
    elif args.command.startswith("build-"):
        build(args.command[6:].title())
    elif args.command == "status":
        print("Selected release:", release_id())
        for role in ("Client", "Server"):
            print(role + " build:", "present" if build_executable(role) else "missing")
    elif args.command == "stop":
        compose("stop")
    else:
        compose("logs", "--tail", "80", "api")


if __name__ == "__main__":
    try:
        main()
    except (RuntimeError, OSError, ValueError, subprocess.CalledProcessError) as error:
        print("Error:", error, file=sys.stderr)
        sys.exit(1)
