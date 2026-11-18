"""Summarize real soak artifacts; do not mix warmup, paused and active tick samples."""
import argparse
import csv
import json
from pathlib import Path
from statistics import mean, median

from summarize_load import windows
from summarize_motion import percentile
from motion_smoke import runtime_errors


def distribution(values):
    return {'samples': len(values), 'mean': mean(values) if values else None,
            'p95': percentile(values, .95), 'p99': percentile(values, .99),
            'max': max(values, default=None)}


def slope(rows, value):
    if len(rows) < 2:
        return None
    xs = [r['elapsed']/60 for r in rows]
    ys = [value(r) for r in rows]
    xm, ym = mean(xs), mean(ys)
    variance = sum((x-xm)**2 for x in xs)
    return sum((x-xm)*(y-ym) for x, y in zip(xs, ys))/variance if variance else None


def summarize(folder):
    samples = json.loads((folder/'samples.json').read_text())
    http = json.loads((folder/'http.json').read_text())
    report = {'directory': str(folder), 'sampleCount': len(samples), 'processes': []}
    all_ticks = []; dense_ticks = []; exact_ticks = []; all_frames = []
    for index, path in enumerate(sorted(folder.glob('server-*.log')), 1):
        # Each new World reports one initial tick, followed by its first 5-second window.
        # Exclude both of those warmup windows, including after checkpoint restore.
        selected = []; dense = []; world_window = 0
        ticks = windows(path, '[ServerTick] samples=', '[ServerTickSamples]')
        for tick in ticks:
            if int(tick['samples']) < 10:
                world_window = 0
            world_window += 1
            if (world_window <= 2 or tick.get('paused') != '0' or tick.get('terminal') != '0' or
                not 280 <= int(tick['samples']) <= 360):
                continue
            if int(tick.get('droppedSamples',0)) or len(tick.get('rawMs',[])) != int(tick['samples']):
                continue
            selected.append(tick)
            if 500 <= int(tick['livingEnemies']) <= 512:
                dense.append(tick)
        raw_ticks = [v for tick in selected for v in tick['rawMs']]
        raw_dense = [v for tick in dense for v in tick['rawMs']]
        raw_exact = [v for tick in dense if int(tick['livingEnemies'])==512 for v in tick['rawMs']]
        all_ticks.extend(raw_ticks); dense_ticks.extend(raw_dense); exact_ticks.extend(raw_exact)
        frames = windows(path, '[Stage11Metrics] frames=', '[Stage11FrameSamples]')[2:]
        raw_frames = [v for frame in frames for v in frame.get('rawMs',[])]
        all_frames.extend(raw_frames)
        frame_count = sum(int(r['frames']) for r in frames)
        # Process IDs are taken from external samples, never inferred from Unity World names.
        pids = list(dict.fromkeys(s['process']['pid'] for s in samples))
        pid = pids[index-1] if index <= len(pids) else None
        rows = [s for s in samples if s['process']['pid'] == pid]
        first = rows[0]['elapsed'] if rows else 0
        stable = [r for r in rows if r['elapsed'] >= first+60 and r['observerAgeSeconds'] <= 2 and
                  r['state'].get('phase') == 'Active' and r['state'].get('paused') == 0 and
                  500 <= r['state'].get('livingEnemies',0) <= 512]
        cpu = [100*(b['process']['cpuSeconds']-a['process']['cpuSeconds'])/(b['elapsed']-a['elapsed'])
               for a,b in zip(rows,rows[1:]) if b['elapsed'] > a['elapsed']]
        rss = lambda r:r['process']['rssKiB']/1024
        managed = lambda r:r['state'].get('managedBytes',0)/1048576
        allocated = lambda r:r['state'].get('unityAllocatedBytes',0)/1048576
        third = max(1,len(stable)//3)
        tick_third = max(1,len(selected)//3)
        first_ticks = [v for t in selected[:tick_third] for v in t['rawMs']]
        last_ticks = [v for t in selected[-tick_third:] for v in t['rawMs']]
        row = {'log':path.name,'pid':pid,'osSamples':len(rows),'densitySamples':len(stable),
            'firstThirdTickMs':distribution(first_ticks),'lastThirdTickMs':distribution(last_ticks),
            'tickBudgetOverrunSamples':sum(v>1000/60 for v in raw_ticks),
            'tickMs':distribution(raw_ticks),'denseTickMs':distribution(raw_dense),'exact512TickMs':distribution(raw_exact),
            'denseTickWindows':len(dense),'excludedTickWindows':len(ticks)-len(selected),'frameMsAllPhases':distribution(raw_frames),
            'cpuPercentOfOneCore':distribution(cpu),
            'rssFirstMiB':rss(rows[0]) if rows else None,'rssLastMiB':rss(rows[-1]) if rows else None,
            'rssPeakMiB':max(map(rss,rows),default=None),
            'rssFirstStableMinuteMiB':median([rss(r) for r in stable if r['elapsed']<stable[0]['elapsed']+60]) if stable else None,
            'rssLastStableMinuteMiB':median([rss(r) for r in stable if r['elapsed']>stable[-1]['elapsed']-60]) if stable else None,
            'rssSlopeMiBPerMinute':slope(stable,rss),'rssLastThirdSlopeMiBPerMinute':slope(stable[-third:],rss),
            'managedFirstStableMinuteMiB':median([managed(r) for r in stable if r['elapsed']<stable[0]['elapsed']+60]) if stable else None,
            'managedLastStableMinuteMiB':median([managed(r) for r in stable if r['elapsed']>stable[-1]['elapsed']-60]) if stable else None,
            'managedPeakMiB':max(map(managed,rows),default=None),
            'unityAllocatedFirstStableMinuteMiB':median([allocated(r) for r in stable if r['elapsed']<stable[0]['elapsed']+60]) if stable else None,
            'unityAllocatedLastStableMinuteMiB':median([allocated(r) for r in stable if r['elapsed']>stable[-1]['elapsed']-60]) if stable else None,
            'gcAllocatedMiB':sum(int(f['gcAllocBytes']) for f in frames)/1048576,
            'gcAllocatedBytesPerFrame':sum(int(f['gcAllocBytes']) for f in frames)/frame_count if frame_count else None,
            'gcCollections':[sum(int(f['gcCollections'].split('/')[i]) for f in frames) for i in range(3)],
            'gcRecorderAvailable':all(f.get('gcRecorder')=='True' for f in frames),
            'counterUnderflowWindows':sum(int(f['netTxBytes'])>2**63 or int(f['netRxBytes'])>2**63 for f in frames),
            'runtimeErrors':runtime_errors(path.read_text(errors='replace')),
            'nativeCrash': 'Native Crash Reporting' in path.read_text(errors='replace'),
            'droppedTickSamples':sum(int(t.get('droppedSamples',0)) for t in ticks)}
        report['processes'].append(row)
    active = [r for r in samples if r['observerAgeSeconds'] <= 2 and r['state'].get('phase')=='Active' and not r['state'].get('paused')]
    report.update(tickMs=distribution(all_ticks),denseTickMs=distribution(dense_ticks),exact512TickMs=distribution(exact_ticks),frameMsAllPhases=distribution(all_frames),
        activeSampleCount=len(active),denseSampleCount=sum(500 <= r['state'].get('livingEnemies',0) <= 512 for r in active),
        exactly512SampleCount=sum(r['state'].get('livingEnemies')==512 for r in active))
    report['http'] = {}
    for action in ('heartbeat','checkpoint','recover','results'):
        rows = [r for r in http if r['path'].endswith('/'+action)]
        accepted = [r for r in rows if r['status']==200]
        report['http'][action]={'statuses':{str(status):sum(r['status']==status for r in rows) for status in {r['status'] for r in rows}},
            'acceptedLatencyMs':distribution([r['seconds']*1000 for r in accepted]),
            'acceptedRequestBytes':distribution([r['requestBytes'] for r in accepted])}
    (folder/'analysis.json').write_text(json.dumps(report,indent=2))
    with (folder/'memory.csv').open('w',newline='') as stream:
        writer=csv.DictWriter(stream,('elapsed','pid','world','phase','paused','livingEnemies','rssMiB','managedMiB','unityAllocatedMiB','gcCollections'))
        writer.writeheader()
        for r in samples:
            s=r['state'];writer.writerow(dict(elapsed=r['elapsed'],pid=r['process']['pid'],world=s.get('world'),phase=s.get('phase'),
                paused=s.get('paused'),livingEnemies=s.get('livingEnemies'),rssMiB=r['process']['rssKiB']/1024,
                managedMiB=s.get('managedBytes',0)/1048576,unityAllocatedMiB=s.get('unityAllocatedBytes',0)/1048576,
                gcCollections=s.get('gcCollections')))
    return report


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('directory',type=Path)
    print(json.dumps(summarize(parser.parse_args().directory),indent=2))
