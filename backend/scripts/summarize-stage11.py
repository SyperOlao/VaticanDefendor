"""Summarize one measure-stage11.ps1 output directory without changing it."""

import argparse
import csv
import json
import re
from pathlib import Path
from statistics import mean


def number(value):
    return float(value.replace(",", "."))


def fields(line):
    return dict(re.findall(r"([A-Za-z][A-Za-z0-9]*)=([^\s.]+)", line))


def summarize_process(directory, run, role, samples):
    index = run["Index"]
    log = directory / f"{role.lower()}-{index}.log"
    text = log.read_text(encoding="utf-8", errors="replace")
    metrics = [fields(line) for line in text.splitlines()
               if line.startswith("[Stage11Metrics] frames=")]
    steady = metrics[1:] if len(metrics) > 1 else metrics
    rows = [row for row in samples if int(row["Index"]) == index and
            row["Role"].lower() == role.lower()]
    cpu = [number(row["CpuPercentOfOneCore"]) for row in rows[1:]]
    result = {
        "pid": run[f"{role}Pid"],
        "crashed": "Crash!!!" in text,
        "cpuMeanPercentOfOneCore": round(mean(cpu), 2) if cpu else None,
        "workingSetMaxMiB": max((number(row["WorkingSetMiB"]) for row in rows), default=None),
        "privateMaxMiB": max((number(row["PrivateMiB"]) for row in rows), default=None),
        "gcAllocatedMiB": round(sum(int(m.get("gcAllocBytes", 0)) for m in metrics) / 1048576, 2),
        "gcCollections": [sum(int(m.get("gcCollections", "0/0/0").split("/")[i])
                              for m in metrics) for i in range(3)],
        "networkTxMiB": round(sum(int(m.get("netTxBytes", 0)) for m in metrics) / 1048576, 2),
        "networkRxMiB": round(sum(int(m.get("netRxBytes", 0)) for m in metrics) / 1048576, 2),
        "receiveQueuePeak": max((int(m.get("rxQueuePeak", 0)) for m in metrics), default=0),
        "sendQueuePeak": max((int(m.get("txQueuePeak", 0)) for m in metrics), default=0),
        "snapshotCounterAvailable": any(m.get("snapshotRecorder") == "True" for m in metrics),
    }
    if role == "Server":
        ticks = [fields(line) for line in text.splitlines()
                 if line.startswith("[ServerTick] samples=")]
        total = sum(int(t["samples"]) for t in ticks)
        result.update({
            "serverTickMeanMs": round(sum(int(t["samples"]) * number(t["avgMs"])
                                            for t in ticks) / total, 3) if total else None,
            "serverTickMaxMs": max((number(t["maxMs"]) for t in ticks), default=None),
            "reachedEnemyCount": max((int(t["livingEnemies"]) for t in ticks), default=0),
            "terminalDuringMeasurement": "Battle finished" in text,
        })
    else:
        result.update({
            "approved": "Connection approved and in game." in text,
            "headlessFrameP95MsMean": round(mean(number(m["p95Ms"]) for m in steady), 2)
            if steady else None,
            "headlessFrameMaxMs": max((number(m["maxMs"]) for m in steady), default=None),
        })
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    args = parser.parse_args()
    directory = args.directory
    runs = json.loads((directory / "runs.json").read_text(encoding="utf-8-sig"))
    if isinstance(runs, dict):
        runs = [runs]
    with (directory / "process-samples.csv").open(encoding="utf-8-sig", newline="") as file:
        samples = list(csv.DictReader(file))
    summary = []
    for run in runs:
        summary.append({"runId": run["RunId"], "port": run["Port"],
                        "server": summarize_process(directory, run, "Server", samples),
                        "client": summarize_process(directory, run, "Client", samples)})
    result = {"directory": str(directory.resolve()), "runs": summary}
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
