"""Summarize motion_smoke results and align opt-in pose traces on the Netcode tick timeline."""
import argparse
from bisect import bisect_left
import csv
import json
import math
from pathlib import Path
import re
import statistics


def percentile(values, quantile):
    return sorted(values)[max(0, math.ceil(len(values) * quantile) - 1)] if values else None


def poses(path):
    samples = {}
    rtt = []
    for line in path.read_text(errors="replace").splitlines():
        if "[MotionTrace]" not in line:
            continue
        header, tail = line.split(" poses=", 1)
        if float(re.search(r"wall=([\d.]+)", header)[1]) < 8:
            continue
        rtt.append(float(re.search(r"rttMs=([\d.]+)", header)[1]))
        for pose in tail.split(" predictedEnemies=", 1)[0].split(";"):
            if not pose:
                continue
            ghost, hero, tick, fraction, x, z, *health = pose.split(",")
            # Fraction 1 is the end of ServerTick; fraction .5 lies halfway from tick-1.
            point = (int(tick) - 1 + float(fraction), float(x), float(z), int(hero), int(health[0]) if health else None)
            samples.setdefault(int(ghost), []).append(point)
    for ghost in samples:
        samples[ghost] = sorted(set(samples[ghost]))
    return samples, rtt


def trace_errors(folder):
    server, _ = poses(folder / "server.log")
    client, rtt = poses(folder / "client.log")
    errors = {0: [], 1: []}
    alive_errors = {0: [], 1: []}
    for ghost, points in client.items():
        truth = server.get(ghost, [])
        ticks = [point[0] for point in truth]
        for tick, x, z, hero, dead in points:
            right = bisect_left(ticks, tick)
            if right == 0 or right >= len(truth):
                continue
            left = truth[right - 1]
            after = truth[right]
            # Reject gaps larger than 200 ms of a 60 Hz simulation (e.g. startup).
            if after[0] - left[0] > 12:
                continue
            fraction = (tick - left[0]) / (after[0] - left[0]) if after[0] != left[0] else 1
            tx = left[1] + (after[1] - left[1]) * fraction
            tz = left[2] + (after[2] - left[2]) * fraction
            error = math.hypot(x - tx, z - tz)
            errors[hero].append(error)
            if dead == 0 and left[4] == 0 and after[4] == 0:
                alive_errors[hero].append(error)
    result = {"measuredRttMedianMs": statistics.median(rtt) if rtt else None}
    for hero, name in ((0, "enemy"), (1, "hero")):
        values = errors[hero]
        result.update({name + "ErrorSamples": len(values), name + "ErrorP95M": percentile(values, .95),
                       name + "ErrorMaxM": max(values) if values else None})
        live = alive_errors[hero]
        result.update({name + "AliveErrorSamples": len(live), name + "AliveErrorP95M": percentile(live, .95),
                       name + "AliveErrorMaxM": max(live) if live else None})
    return result


def summarize(folder):
    result = json.loads((folder / "result.json").read_text())
    row = {key: result[key] for key in ("label", "requestedEnemies", "requestedRttMs", "lossPercentPerDirection", "graphical", "trace", "inGame")}
    metrics = []
    for line in result["clientMetrics"]:
        fields = dict(re.findall(r"(\w+)=([^ ]+)", line))
        if "frames" in fields:
            metrics.append(fields)
    # First report includes native player initialization, second may still include joining/spawn.
    steady = metrics[2:] if result["inGame"] else []
    frames = sum(int(m["frames"]) for m in steady)
    seconds = sum(float(m["avgMs"]) * int(m["frames"]) / 1000 for m in steady)
    row.update(steadyWindows=len(steady), basicPassed=result.get("basicPassed", result["inGame"] and not result["clientErrors"] and not result["serverErrors"]), fps=frames / seconds if seconds else None,
               worstWindowP95Ms=max((float(m["p95Ms"]) for m in steady), default=None),
               maxFrameMs=max((float(m["maxMs"]) for m in steady), default=None),
               gcBytesPerFrame=sum(int(m["gcAllocBytes"]) for m in steady) / frames if frames else None,
               txKbitS=sum(int(m["netTxBytes"]) for m in steady) * 8 / 1000 / seconds if seconds else None,
               rxKbitS=sum(int(m["netRxBytes"]) for m in steady) * 8 / 1000 / seconds if seconds else None,
               clientErrors=len(result["clientErrors"]), serverErrors=len(result["serverErrors"]))
    rates = [float(m["rttMs"]) for m in steady if "rttMs" in m]
    row["measuredRttMedianMs"] = statistics.median(rates) if rates else None
    living = [int(value) for line in result["serverMetrics"] for value in re.findall(r"livingEnemies=(\d+)", line) if int(value) > 0]
    row["livingEnemiesMin"] = min(living) if living else None
    row["livingEnemiesMax"] = max(living) if living else None
    content = (folder / "client.log").read_text(errors="replace")
    first_response = re.search(r"\[MotionProbe\] responseMs=([\d.]+) segment=0", content)
    row["initialInputResponseMs"] = float(first_response[1]) if first_response else None
    cpu = []
    memory = []
    for sample in result["osSamples"]:
        lines = sample["ps"].splitlines()
        if sample["seconds"] >= 8 and len(lines) == 2:
            fields = lines[-1].split()
            cpu.append(float(fields[1])); memory.append(int(fields[2]) / 1024)
    row["clientProcessCpuMedianPercent"] = statistics.median(cpu) if cpu and result["inGame"] else None
    row["clientRssMaxMiB"] = max(memory) if memory and result["inGame"] else None
    if result["trace"]:
        row.update(trace_errors(folder))
    return row


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    args = parser.parse_args()
    rows = [summarize(path.parent) for path in sorted(args.directory.glob("*/result.json"))]
    (args.directory / "summary.json").write_text(json.dumps(rows, indent=2))
    keys = list(dict.fromkeys(key for row in rows for key in row))
    with (args.directory / "summary.csv").open("w") as output:
        writer = csv.DictWriter(output, keys)
        writer.writeheader(); writer.writerows(rows)
    print(json.dumps(rows, indent=2))


if __name__ == "__main__":
    main()
