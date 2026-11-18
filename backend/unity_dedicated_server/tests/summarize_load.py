"""Pool measured distributions, retaining failed cases and unavailable counters.

The first two windows of each process include startup/spawn and are excluded from
steady results. Tick timings are SimulationSystemGroup wall intervals, not total
process CPU or completion times for every asynchronous worker. Density is the
living count observed at each selected server-window endpoint.
"""
import argparse
import csv
import json
from pathlib import Path
import re
from statistics import mean
from summarize_motion import percentile, trace_errors


def fields(line):
    return {key: value.rstrip('.') for key, value in re.findall(r'(\w+)=([^ ]+)', line)}


def windows(path, marker, raw_marker):
    rows = []
    current = None
    for line in path.read_text(errors='replace').splitlines():
        if line.startswith(marker):
            current = fields(line)
            rows.append(current)
        elif line.startswith(raw_marker) and current is not None:
            current['rawMs'] = [int(value) / 1000 for value in line.split('durationUs=', 1)[1].split(',') if value]
    return rows


def summarize(folder):
    result = json.loads((folder / 'result.json').read_text())
    row = {key: result.get(key) for key in ('label', 'requestedEnemies', 'requestedRttMs',
        'lossPercentPerDirection', 'predictedEnemyBudget', 'graphical', 'basicPassed',
        'connectObservedSeconds', 'connectObservationResolutionSeconds', 'trace', 'profileSystems', 'correctionProbe', 'inGame')}
    row['caseDirectory'] = str(folder)
    row['connectObservationResolutionSeconds'] = max((b['seconds'] - a['seconds'] for a, b in
        zip(result['osSamples'], result['osSamples'][1:])), default=None)
    os_start = max(10, (result.get('connectObservedSeconds') or 0) + 5)
    row['osMeasurementStartSeconds'] = os_start
    for role in ('server', 'client'):
        path = folder / (role + '.log')
        frames = windows(path, '[Stage11Metrics] frames=', '[Stage11FrameSamples]')[2:]
        seconds = sum(float(m['avgMs']) * int(m['frames']) / 1000 for m in frames)
        count = sum(int(m['frames']) for m in frames)
        raw = [value for m in frames for value in m.get('rawMs', [])]
        row[role + 'Windows'] = len(frames)
        row[role + 'MeasuredSeconds'] = seconds
        row[role + 'Fps'] = count / seconds if seconds else None
        row[role + 'FrameP95Ms'] = percentile(raw, .95)
        row[role + 'FrameP99Ms'] = percentile(raw, .99)
        row[role + 'GcBytesPerFrame'] = sum(int(m['gcAllocBytes']) for m in frames) / count if count else None
        row[role + 'GcCollections'] = [sum(int(m['gcCollections'].split('/')[i]) for m in frames) for i in range(3)]
        for field in ('netTxBytes', 'netRxBytes'):
            row[role + field[0].upper() + field[1:] + 'PerSecond'] = sum(int(m[field]) for m in frames) / seconds if seconds else None
        row[role + 'Snapshots'] = sum(int(m['snapshots']) for m in frames) if frames and all('snapshots' in m for m in frames) else None
        row[role + 'SnapshotCounterReset'] = any(m.get('snapshotCounterReset') == 'True' for m in frames)
        row[role + 'SnapshotBytesAvailable'] = any(m.get('snapshotRecorder') == 'True' for m in frames)
        if role == 'client':
            row['rttMeanMs'] = mean(float(m['rttMs']) for m in frames) if frames else None
        else:
            ticks = windows(path, '[ServerTick] samples=', '[ServerTickSamples]')[2:]
            active = [m for m in ticks if m.get('paused', '0') == '0' and m.get('terminal', '0') == '0']
            raw = [value for m in active for value in m.get('rawMs', [])]
            count = sum(int(m['samples']) for m in active)
            density = [m for m in active if int(m['livingEnemies']) == result.get('requestedEnemies')]
            density_samples = [value for m in density for value in m.get('rawMs', [])]
            row['fullDensityTickWindows'] = len(density)
            row['fullDensityTickSamples'] = len(density_samples)
            row['fullDensityTickMeanMs'] = mean(density_samples) if density_samples else None
            row['fullDensityTickP95Ms'] = percentile(density_samples, .95)
            row['fullDensityTickP99Ms'] = percentile(density_samples, .99)
            row['tickSamples'] = count
            row['tickRawSamples'] = len(raw)
            row['tickSampleOverflow'] = sum(int(m.get('droppedSamples', 0)) for m in active)
            row['tickMeanMs'] = sum(int(m['samples']) * float(m['avgMs']) for m in active) / count if count else None
            row['tickP95Ms'] = percentile(raw, .95)
            row['tickP99Ms'] = percentile(raw, .99)
            row['tickMaxMs'] = max((float(m['maxMs']) for m in active), default=None)
            living = [int(m['livingEnemies']) for m in active]
            row['activeWindowLivingMin'] = min(living) if living else None
            row['activeWindowLivingMax'] = max(living) if living else None
            row['excludedInactiveTickWindows'] = len(ticks) - len(active)
        samples = [value for sample in result['osSamples'] if sample['seconds'] >= os_start
                   for value in sample.get('intervals', []) if value['role'] == role]
        cpu = [value['cpuPercentOfOneCore'] for value in samples if value['cpuPercentOfOneCore'] is not None]
        row[role + 'CpuMeanPercentOfCore'] = mean(cpu) if cpu else None
        row[role + 'CpuP95PercentOfCore'] = percentile(cpu, .95)
        row[role + 'RssMaxMiB'] = max((value['rssKiB'] / 1024 for value in samples), default=None)
        row[role + 'Errors'] = result.get(role + 'Errors', [])
        system = {}
        window = 0
        for line in path.read_text(errors='replace').splitlines():
            if line.startswith('[Stage11Metrics] frames='): window += 1
            if window < 3 or not line.startswith('[Stage11System]'): continue
            match = re.search(r'name=(.*?) totalNs=(\d+) frames=(\d+) valid=(\w+)', line)
            if not match: continue
            name, total, frames, valid = match.groups()
            values = system.setdefault(name, [0, 0])
            values[0] += int(total); values[1] += int(frames)
        row[role + 'InclusiveSystemMeanMsPerFrame'] = {name: total / frames / 1e6 if frames else None
                                                      for name, (total, frames) in system.items()}
    content = (folder / 'client.log').read_text(errors='replace')
    selected = [int(value) for value in re.findall(r'\[EnemyPrediction\] selected=(\d+)', content)]
    row['observedSelectedEnemiesMax'] = max(selected, default=None)
    responses = [float(value) for value in re.findall(r'\[MotionProbe\] responseMs=([\d.]+)', content)]
    row['inputResponses'] = len(responses)
    row['inputResponseP95Ms'] = percentile(responses, .95)
    row['activeMeasurementValid'] = bool(result.get('basicPassed') and row.get('tickRawSamples', 0) > 0 and
                                         row.get('tickRawSamples') == row.get('tickSamples') and
                                         row.get('tickSampleOverflow', 0) == 0 and
                                         row.get('activeWindowLivingMax', 0) > 0)
    if not result.get('inGame'):
        # Keep idle process counters as observations, but never present the menu FPS as gameplay FPS.
        row['clientIdleObservedFps'] = row['clientFps']
        row['clientFps'] = None
    corrections = [fields(line) for line in content.splitlines() if line.startswith('[CorrectionProbe]')][2:]
    correction_seconds = sum(float(value['seconds']) for value in corrections)
    row['correctionProbeSeconds'] = correction_seconds
    row['heroSmoothingCorrectionsPerSecond'] = sum(int(value['heroCorrections']) for value in corrections) / correction_seconds if correction_seconds else None
    row['enemySmoothingCorrectionsPerSecond'] = sum(int(value['enemyCorrections']) for value in corrections) / correction_seconds if correction_seconds else None
    row['correctionProbeOverflow'] = sum(int(value['overflow']) for value in corrections)
    if result.get('trace'): row.update(trace_errors(folder))
    return row


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=Path)
    args = parser.parse_args()
    rows = [summarize(path.parent) for path in sorted(args.directory.rglob('result.json'))]
    (args.directory / 'load-summary.json').write_text(json.dumps(rows, indent=2))
    keys = list(dict.fromkeys(key for row in rows for key in row))
    with (args.directory / 'load-summary.csv').open('w', newline='') as output:
        writer = csv.DictWriter(output, keys)
        writer.writeheader(); writer.writerows(rows)
    print(json.dumps([{k: v for k, v in row.items() if not isinstance(v, (dict, list))} for row in rows], indent=2))


if __name__ == '__main__':
    main()
