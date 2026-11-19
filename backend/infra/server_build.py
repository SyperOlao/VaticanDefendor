"""Build a host-native Dedicated Server from the local Unity project's inputs."""
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile

INPUT_FOLDERS = ("Assets", "Packages", "ProjectSettings")
GENERATED_ASSETS = {"SceneDependencyCache", "SceneDependencyCache.meta",
                    "netcode-build-assets-temp", "netcode-build-assets-temp.meta"}


def unity_inputs(root, *, allow_empty=False):
    files = {}
    for folder in INPUT_FOLDERS:
        for current, dirs, names in os.walk(root / folder):
            directory = Path(current)
            ignored = {".DS_Store"}
            if directory == root / "Assets":
                ignored |= GENERATED_ASSETS
            dirs[:] = [name for name in dirs if name not in ignored]
            for name in names:
                if name not in ignored:
                    path = directory / name
                    files[path.relative_to(root)] = hashlib.sha256(path.read_bytes()).digest()
    if not files and not allow_empty:
        raise RuntimeError(f"No Unity build inputs in {root}.")
    return files


def fingerprint(files):
    digest = hashlib.sha256()
    for path in sorted(files):
        digest.update(os.fsencode(path))
        digest.update(b"\0")
        digest.update(files[path])
    return digest.hexdigest()


def editor_for(root, override):
    version_file = root / "ProjectSettings/ProjectVersion.txt"
    match = re.search(r"^m_EditorVersion:\s*(\S+)", version_file.read_text(), re.M)
    if not match:
        raise RuntimeError(f"Cannot read Unity Editor version from {version_file}.")
    version = match[1]
    if override:
        editor = Path(override).expanduser()
        candidates = [root / editor]
    elif sys.platform == "darwin":
        candidates = [folder / f"Unity/Hub/Editor/{version}/Unity.app/Contents/MacOS/Unity"
                      for folder in (Path("/Applications"), Path.home() / "Applications")]
    else:
        candidates = [Path.home() / f"Unity/Hub/Editor/{version}/Editor/Unity"]
    for editor in candidates:
        if editor.is_file() and os.access(editor, os.X_OK):
            return editor.resolve()
    raise RuntimeError(f"Unity Editor {version} is required to build the server. "
                       "Install it with Dedicated Server build support or set VD_UNITY_EDITOR.")


def sync_project(root, project, inputs):
    for folder in INPUT_FOLDERS:
        (project / folder).mkdir(parents=True, exist_ok=True)
    existing = unity_inputs(project, allow_empty=True)
    for path in existing.keys() - inputs.keys():
        (project / path).unlink()
    for path, digest in inputs.items():
        if existing.get(path) != digest:
            target = project / path
            if target.is_dir():
                shutil.rmtree(target)
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(root / path, target)
    if unity_inputs(project) != inputs:
        raise RuntimeError("Unity project changed while copying build inputs. Run up.sh again.")


def binary_in(output):
    if sys.platform == "darwin":
        metadata = output / "Data/app.info"
        if not metadata.is_file():
            return None
        lines = metadata.read_text().splitlines()
        if len(lines) < 2 or not lines[1] or Path(lines[1]).name != lines[1]:
            return None
        binary = output / lines[1]
        engine = output / "UnityPlayer.dylib"
        data = output / "Data"
    else:
        binary = output / "VaticanDefendorServer.x86_64"
        engine = output / "UnityPlayer.so"
        data = output / "VaticanDefendorServer_Data"
    if not engine.is_file() or not (data / "Managed/VaticanDefendor.Network.Contracts.dll").is_file():
        return None
    return binary if binary.is_file() and os.access(binary, os.X_OK) else None


def prepare(root, runtime, editor_override):
    if sys.platform not in ("darwin", "linux"):
        raise RuntimeError("Automatic local Dedicated Server builds require macOS or Linux.")
    inputs = unity_inputs(root)
    stamp = fingerprint(inputs)
    receipts = runtime / "server-build.json"
    builds = runtime / "builds"
    if receipts.is_file():
        receipt = json.loads(receipts.read_text())
        if receipt["fingerprint"] == stamp:
            output = builds / receipt["artifact"]
            if output.parent == builds and (binary := binary_in(output)):
                print(f"Using current Dedicated Server build ({stamp[:12]}).", flush=True)
                return binary

    editor = editor_for(root, editor_override)
    project = runtime / "unity-project"
    sync_project(root, project, inputs)
    log = runtime / "build.log"
    log.write_text("")
    args = [str(editor), "-batchmode", "-nographics", "-quit", "-projectPath", str(project),
            "-standaloneBuildSubtarget", "Server", "-logFile", str(log)]
    if sys.platform == "darwin":
        method = "BuildMacServer"
        output = project / "Builds/OnlineMac/Server/VaticanDefendor.app"
    else:
        args.extend(("-buildTarget", "Linux64"))
        method = "BuildLinuxServer"
        output = project / "backend/unity_dedicated_server/build/linux-server"
    args.extend(("-executeMethod", "VaticanDefendor.Editor.NetworkPrototypeBuild." + method))
    print(f"Building Dedicated Server with Unity. Log: {log}", flush=True)
    result = subprocess.run(args, cwd=project, check=False)
    if result.returncode or not binary_in(output) or "[NetworkBuild] Server:" not in log.read_text(errors="replace"):
        raise RuntimeError(f"Unity Dedicated Server build failed. See {log}.")
    if unity_inputs(root) != inputs:
        raise RuntimeError("Unity project changed during the build. Run up.sh again to build the latest sources.")

    builds.mkdir(parents=True, exist_ok=True)
    artifact = Path(tempfile.mkdtemp(prefix=stamp[:12] + "-", dir=builds))
    shutil.copytree(output, artifact, dirs_exist_ok=True)
    binary = binary_in(artifact)
    if binary is None:
        raise RuntimeError("Copied Dedicated Server build is incomplete.")
    with tempfile.NamedTemporaryFile("w", dir=runtime, delete=False) as stream:
        json.dump({"fingerprint": stamp, "artifact": artifact.name}, stream)
        temporary = Path(stream.name)
    temporary.replace(receipts)
    return binary
