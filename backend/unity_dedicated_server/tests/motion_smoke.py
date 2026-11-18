"""Run real existing Unity players; results are measurements, not automatic smoothness approval.

Python 3 standard library only. Development players and a graphical desktop are required.
The UTP simulator remains the latency/loss injector. The optional UDP relay only holds packets
for the bounded stall scenario; it implements no game/network protocol.
"""
import argparse
import json
import os
from pathlib import Path
import re
import select
import signal
import socket
import subprocess
import threading
import time
import uuid
from concurrent.futures import ThreadPoolExecutor


def free_port():
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


class PacketHold:
    def __init__(self, server_port, duration):
        self.socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.socket.bind(("127.0.0.1", 0))
        self.port = self.socket.getsockname()[1]
        self.server = ("127.0.0.1", server_port)
        self.duration = duration
        self.stop = threading.Event()
        self.held = 0
        self.started = 0

    def run(self):
        client = None
        packets = []
        self.started = time.monotonic()
        while not self.stop.is_set():
            elapsed = time.monotonic() - self.started
            holding = self.duration / 2 <= elapsed < self.duration / 2 + 0.75
            if not holding and packets:
                for data, target in packets:
                    self.socket.sendto(data, target)
                packets.clear()
            if not select.select([self.socket], [], [], 0.01)[0]:
                continue
            data, source = self.socket.recvfrom(65535)
            if source == self.server:
                if client is None:
                    continue
                target = client
            else:
                client = source
                target = self.server
            if holding:
                packets.append((data, target))
                self.held += 1
            else:
                self.socket.sendto(data, target)
        self.socket.close()


def runtime_errors(content):
    # Exception in an ECS method name inside an ordinary warning stack is not an exception.
    pattern = r"^(?:[A-Za-z_][\w.]*Exception|Exception):|\bFAIL\b|Crash!!!|BadProtocolVersion|ENTITY_NOT_FOUND"
    return [line for line in content.splitlines() if re.search(pattern, line)]


def cpu_seconds(value):
    """ps TIME is cumulative user+system CPU (unlike its smoothed %CPU column)."""
    parts = value.split(":")
    return sum(float(part) * 60 ** index for index, part in enumerate(reversed(parts)))


def run_case(args, enemies, rtt, loss, stall=False, reconnect=False):
    label = f"n{enemies}-rtt{rtt}-loss{loss}" + ("-hold" if stall else "") + ("-reconnect" if reconnect else "")
    folder = args.output / label
    folder.mkdir(parents=True, exist_ok=True)
    port = free_port()
    previous = getattr(args, "reuse_run_ids", None)
    run_id = str(uuid.UUID(json.loads((previous / label / "result.json").read_text())["runId"])) if previous else str(uuid.uuid4())
    env = os.environ.copy()
    env.update(VD_DEV_TICKET=uuid.uuid4().hex + uuid.uuid4().hex, VD_DEV_ACCOUNT_ID="motion-measurement")
    common = ["-vdDevelopmentIdentity", "-vdRunId", run_id, "-vdMetrics"]
    if getattr(args, "metric_samples", False):
        common.append("-vdMetricSamples")
    if getattr(args, "profile_systems", False):
        common.append("-vdProfileSystems")
    if args.trace:
        common.append("-vdMotionTrace")
    server_log = folder / "server.log"
    client_log = folder / "client.log"
    server_args = [str(args.server), "-batchmode", "-nographics", "-vdRole", "server", "-vdPort", str(port),
                   "-vdLoadLevel", {32: "small", 128: "medium", 512: "large"}[enemies],
                   "-logFile", str(server_log), *common]
    simulation_hz = getattr(args, "simulation_hz", 60)
    snapshot_hz = getattr(args, "snapshot_hz", 30)
    server_args += ["-vdSimulationHz", str(simulation_hz), "-vdSnapshotHz", str(snapshot_hz)]
    relay = PacketHold(port, args.seconds) if stall else None
    relay_thread = None
    if relay:
        relay_thread = threading.Thread(target=relay.run, daemon=True)
        relay_thread.start()
    client_args = [str(args.client), "-vdRole", "client", "-vdHost", "127.0.0.1", "-vdPort", str(relay.port if relay else port),
                   "-vdMove", getattr(args, "scripted_move", "0.5,0"), "-logFile", str(client_log), *common]
    client_args += ["-vdPredictedEnemies", str(getattr(args, "predicted_enemies", 32))]
    if args.headless:
        client_args += ["-batchmode", "-nographics"]
    if args.motion_probe:
        client_args.append("-vdMotionProbe")
    if getattr(args, "correction_probe", False):
        client_args.append("-vdCorrectionProbe")
    if reconnect:
        client_args.append("-vdProbeReconnect")
    if rtt or loss:
        profile = dict(MaxPacketCount=1000, MaxPacketSize=1400, RandomSeed=42, Mode=2,
                       PacketDelayMs=rtt // 2, PacketJitterMs=min(10, rtt // 2),
                       PacketDropInterval=0, PacketDropPercentage=loss, PacketDuplicationPercentage=0,
                       FuzzFactor=0, FuzzOffset=0)
        path = folder / "simulator.json"
        path.write_text(json.dumps(profile, indent=2))
        client_args += ["--loadNetworkSimulatorJsonFile", str(path)]
    processes = []
    samples = []
    result = dict(label=label, requestedEnemies=enemies, requestedRttMs=rtt, lossPercentPerDirection=loss,
                  graphical=not args.headless, trace=args.trace, runId=run_id,
                  serverSimulationHz=simulation_hz, serverSnapshotHz=snapshot_hz,
                  predictedEnemyBudget=getattr(args, "predicted_enemies", 32),
                  metricSamples=getattr(args, "metric_samples", False),
                  profileSystems=getattr(args, "profile_systems", False),
                  correctionProbe=getattr(args, "correction_probe", False),
                  scriptedMove=getattr(args, "scripted_move", "0.5,0"), motionProbe=args.motion_probe)
    try:
        processes.append(subprocess.Popen(server_args, env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL))
        time.sleep(2)
        processes.append(subprocess.Popen(client_args, env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL))
        started = time.monotonic()
        if relay:
            result["holdAtClientSeconds"] = relay.started + args.seconds / 2 - started
            result["holdDurationSeconds"] = 0.75
        approved_at = None
        previous_cpu = {}
        while time.monotonic() - started < args.seconds and all(p.poll() is None for p in processes):
            # ps %CPU is the OS process average (100% = one logical CPU), not Unity frame time.
            row = subprocess.run(["ps", "-p", ",".join(str(p.pid) for p in processes), "-o", "pid=,%cpu=,rss=,time="],
                                 text=True, capture_output=True, check=False)
            now = time.monotonic()
            intervals = []
            for line in row.stdout.splitlines():
                pid, smoothed, rss, cumulative = line.split()
                consumed = cpu_seconds(cumulative)
                prior = previous_cpu.get(pid)
                intervals.append(dict(pid=int(pid), role="server" if int(pid) == processes[0].pid else "client",
                    cpuPercentOfOneCore=100 * (consumed - prior[1]) / (now - prior[0]) if prior else None,
                    rssKiB=int(rss), cumulativeCpuSeconds=consumed))
                previous_cpu[pid] = (now, consumed)
            if approved_at is None and client_log.exists() and "Connection approved and in game" in client_log.read_text(errors="replace"):
                approved_at = now - started
            samples.append(dict(seconds=now - started, ps=row.stdout.strip(), intervals=intervals))
            time.sleep(1)
        result["connectObservedSeconds"] = approved_at
        result["connectObservationResolutionSeconds"] = max((b["seconds"] - a["seconds"] for a, b in zip(samples, samples[1:])), default=None)
        result["elapsedSeconds"] = time.monotonic() - started
        result["exitBeforeStop"] = [p.poll() for p in processes]
    finally:
        for process in reversed(processes):
            if process.poll() is None:
                process.send_signal(signal.SIGTERM)
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()
        if relay:
            relay.stop.set()
            relay_thread.join(timeout=2)
            result["heldPackets"] = relay.held
        result["exitCodesAfterStop"] = [p.returncode for p in processes]
        result["osSamples"] = samples
        for role, log in (("client", client_log), ("server", server_log)):
            content = log.read_text(errors="replace") if log.exists() else ""
            result[role + "Metrics"] = [line for line in content.splitlines() if "[Stage11Metrics]" in line or "[ServerTick]" in line]
            result[role + "Errors"] = runtime_errors(content)
            if role == "client":
                result["inGame"] = "Connection approved and in game" in content
                result["reconnectPassed"] = "[ReconnectProbe] PASS" in content
                result["clientDisconnects"] = content.count("[Client] Disconnected:")
        (folder / "result.json").write_text(json.dumps(result, indent=2))
        alive = result.get("exitBeforeStop", []) == [None, None]
        expected_exit = reconnect and result.get("reconnectPassed", False)
        result["basicPassed"] = result.get("inGame", False) and not result.get("clientErrors") and not result.get("serverErrors") and (alive or expected_exit) and (not reconnect or expected_exit) and (reconnect or not result.get("clientDisconnects", 0))
        (folder / "result.json").write_text(json.dumps(result, indent=2))
        print(json.dumps({key: value for key, value in result.items() if key not in ("osSamples", "clientMetrics", "serverMetrics")}), flush=True)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", type=Path, required=True)
    parser.add_argument("--client", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--seconds", type=int, default=40)
    parser.add_argument("--simulation-hz", type=int, default=60)
    parser.add_argument("--snapshot-hz", type=int, default=30)
    parser.add_argument("--enemies", type=int, nargs="+", choices=(32, 128, 512), default=[32, 128, 512])
    parser.add_argument("--rtt", type=int, nargs="+", choices=(0, 100, 200, 300), default=[0, 100, 200, 300])
    parser.add_argument("--loss", type=int, nargs="+", choices=(0, 1, 5), default=[0, 1, 5])
    parser.add_argument("--reuse-run-ids", type=Path,
                        help="Replay matching case RunIds from an earlier output directory, preserving the simulation RNG seed")
    parser.add_argument("--predicted-enemies", type=int, choices=(0, 32), default=32,
                        help="0 interpolates all enemies; hero remains owner-predicted in both modes")
    parser.add_argument("--metric-samples", action="store_true", help="Raw distributions; identical instrumentation in before/after runs")
    parser.add_argument("--correction-probe", action="store_true", help="Observe actual prediction smoothing adjustments; dependency barriers make this a separate diagnostic run")
    parser.add_argument("--profile-systems", action="store_true", help="Individual inclusive ECS profiler markers; use with --metric-samples")
    parser.add_argument("--matches", type=int, choices=range(1, 7), default=1,
                        help="Independent server/client process pairs, not matches sharing one ECS World")
    parser.add_argument("--scripted-move", default="0.5,0", help="Existing -vdMove input; use 0,0 without --motion-probe for a stationary deterministic CPU comparison")
    parser.add_argument("--headless", action="store_true")
    parser.add_argument("--motion-probe", action="store_true")
    parser.add_argument("--trace", action="store_true", help="Extra diagnostic allocations; use separate runs for clean GC measurements")
    parser.add_argument("--hold", action="store_true", help="Hold both directions for 750 ms halfway through the run")
    parser.add_argument("--reconnect", action="store_true")
    args = parser.parse_args()
    args.output = args.output.resolve()
    args.server = args.server.resolve(strict=True)
    args.client = args.client.resolve(strict=True)
    if not (30 <= args.simulation_hz <= 120 and 10 <= args.snapshot_hz <= args.simulation_hz and args.simulation_hz % args.snapshot_hz == 0):
        parser.error("Simulation Hz must be 30..120 and divisible by snapshot Hz (10..simulation Hz).")
    if args.seconds < 15:
        parser.error("Use at least 15 seconds to include a steady-state metrics window.")
    failed = False
    for enemies in args.enemies:
        for rtt in args.rtt:
            for loss in args.loss:
                if args.matches == 1:
                    results = [run_case(args, enemies, rtt, loss, args.hold, args.reconnect)]
                else:
                    import copy
                    jobs = []
                    with ThreadPoolExecutor(max_workers=args.matches) as pool:
                        for match in range(args.matches):
                            configured = copy.copy(args)
                            configured.output = args.output / f"match-{match + 1}"
                            if args.reuse_run_ids:
                                configured.reuse_run_ids = args.reuse_run_ids / f"match-{match + 1}"
                            jobs.append(pool.submit(run_case, configured, enemies, rtt, loss, args.hold, args.reconnect))
                        results = [job.result() for job in jobs]
                failed |= any(not result["basicPassed"] for result in results)
    if failed:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
