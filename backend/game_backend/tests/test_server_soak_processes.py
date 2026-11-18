"""Opt-in sustained real-player / FastAPI / PostgreSQL soak. No development identity.

Reuses the existing recovery fixture, typed checkpoints and reward consumers.
VD_SOAK_SECONDS=1800 is the review run; a shorter preflight is labelled separately.
"""
import json
import os
from pathlib import Path
import subprocess
import time

import pytest
from sqlalchemy import select, func

from .test_recovery_processes import Player, process_case, wait_for

pytestmark = pytest.mark.skipif(
    not os.environ.get('VD_SOAK_SECONDS') or not os.environ.get('VD_RECOVERY_PROJECT'),
    reason='Opt-in native soak requires built players, disposable PostgreSQL and VD_SOAK_SECONDS')


def process_sample(player):
    result = subprocess.run(['ps', '-p', str(player.process.pid), '-o', 'rss=,time='],
                            capture_output=True, text=True, check=True).stdout.split()
    minutes, seconds = result[1].split(':')
    return {'pid': player.process.pid, 'rssKiB': int(result[0]),
            'cpuSeconds': int(minutes) * 60 + float(seconds)}


def test_sustained_512_backend_checkpoint_recovery(process_case):
    from vatican_backend.application.energy import ConfigureEnergy, EnergyService
    from vatican_backend.domain.energy import EnergyRules
    from vatican_backend.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory
    from vatican_backend.persistence.postgres.tables import Run, GameServer, RunCheckpoint, RunResultOperation, RunEnergyCharge

    c = process_case
    duration = int(os.environ['VD_SOAK_SECONDS'])
    crash_after = float(os.environ.get('VD_SOAK_CRASH_AFTER_SECONDS', duration/2))
    assert 0 < crash_after < duration
    assert duration >= 120, 'Shorter runs cannot exercise ordinary 30-second checkpoints and recovery'
    c.retention = 120
    # Isolated fixture configuration, never product tuning. Enough energy for natural match cycles.
    ConfigureEnergy(PostgresUnitOfWorkFactory(c.database)).execute(
        EnergyRules('soak-energy-v1', 10000, 60_000, 1, 5))
    use_cases = c.client.app.state.use_cases
    use_cases.runs.energy = EnergyService('soak-energy-v1')
    use_cases.energy.service = use_cases.runs.energy
    events, samples = [], []
    paths = {name: c.directory / (name + '.json') for name in ('events', 'samples', 'http', 'summary')}

    def event(name, **values):
        entry = dict(name=name, elapsed=round(time.monotonic() - started, 3), **values)
        events.append(entry)
        paths['events'].write_text(json.dumps(events, indent=2))
        print(json.dumps(entry), flush=True)

    def save():
        paths['samples'].write_text(json.dumps(samples))
        paths['http'].write_text(json.dumps(list(c.http_events)))

    def sql_summary():
        with c.database.transaction() as session:
            rows = list(session.scalars(select(Run).order_by(Run.created_at)))
            server_row = session.get(GameServer, c.slot.server_id)
            return {'runs': [{'id': str(r.id), 'status': r.status, 'generation': r.generation,
                              'outcome': r.outcome, 'prestige': r.permanent_reward} for r in rows],
                    'results': session.scalar(select(func.count()).select_from(RunResultOperation)),
                    'energyCharges': session.scalar(select(func.count()).select_from(RunEnergyCharge)),
                    'energyCharged': session.scalar(select(func.sum(RunEnergyCharge.cost))),
                    'lastSeen': server_row.last_seen.isoformat() if server_row else None}

    env = {'VD_CHECKPOINT_SECONDS': '30', 'VD_RECOVERY_SAMPLE_SECONDS': '1'}
    extra = ('-vdMetrics', '-vdMetricSamples')
    started = time.monotonic()
    server = Player(c, 'server', 'server-1', extra=extra, env_overrides=env)
    wait_for(c.db, lambda x: x['serverStatus'] == 'ready')
    client_index = 0

    def connect():
        nonlocal client_index
        client_index += 1
        client = Player(c, 'client', f'client-{client_index}', extra=extra, env_overrides=env)
        joined = client.wait(lambda x: x.get('connection') == 'InGame' and x.get('heroes') == 1, 60)
        server.send('load512')
        populated = server.wait(lambda x: x.get('livingEnemies', 0) >= 500, 30)
        event('joined', client=client.name, state=populated, joinSeconds=round(time.monotonic()-join_started[0], 3))
        return client

    join_started = [time.monotonic()]
    client = connect()
    measured_started = time.monotonic()
    crash_done = False
    next_sample = measured_started
    next_heartbeat_print = measured_started
    try:
        while time.monotonic() - measured_started < duration:
            now = time.monotonic()
            assert server.process.poll() is None, server.log
            state = server.read()
            assert not state.get('error'), state
            if now >= next_sample:
                db = c.db()
                checkpoint = db.pop('checkpoint')
                samples.append({'elapsed': round(now-measured_started, 3), 'process': process_sample(server),
                    'state': state, 'client': client.read(), 'db': db, 'sql': sql_summary(),
                    'checkpointTick': checkpoint.get('simulationTick') if checkpoint else None,
                    'checkpointSequence': checkpoint.get('checkpointSequence') if checkpoint else None,
                    'observerAgeSeconds': round(time.time()-server.output.stat().st_mtime, 3)})
                save()
                next_sample = now + 5
            if now >= next_heartbeat_print:
                print(json.dumps({'elapsed': round(now-measured_started), 'server': server.name,
                    'phase': state.get('phase'), 'living': state.get('livingEnemies'),
                    'rssMiB': round(samples[-1]['process']['rssKiB']/1024, 1),
                    'managedMiB': round(state.get('managedBytes', 0)/1048576, 1)}), flush=True)
                next_heartbeat_print = now + 30
            # Natural results are settled through the existing consumer implementations / SQL inboxes.
            c.drain()
            if state.get('phase') == 'Completed':
                event('natural-match-completed', state=state, sql=sql_summary())
                client.close()
                wait_for(c.db, lambda x: x['serverStatus'] == 'ready', 45)
                join_started[0] = time.monotonic()
                client = connect()
            elif (not crash_done and now-measured_started >= crash_after and
                  state.get('phase') == 'Active' and state.get('simulationSeconds', 999) < 15):
                # Freeze an exact confirmed reference, then run ten more seconds before SIGKILL.
                client.send('pause')
                frozen = server.wait(lambda x: x.get('paused') == 1)
                charges_before = sql_summary()['energyCharges']
                saved = wait_for(c.db, lambda x: x['checkpoint'] is not None and
                    x['checkpoint'].get('manualPaused') == 1 and
                    x['checkpoint']['simulationTick'] >= frozen['tick'], 40)
                checkpoint = saved['checkpoint']
                (c.directory/'crash-confirmed-checkpoint.json').write_text(json.dumps(checkpoint))
                client.send('unpause')
                server.wait(lambda x: x.get('paused') == 0)
                time.sleep(10)
                before_crash = server.read()
                assert before_crash['phase'] == 'Active', before_crash
                actual = c.db()
                assert actual['checkpoint']['checkpointSequence'] == checkpoint['checkpointSequence']
                event('sigkill', state=before_crash, checkpointTick=checkpoint['simulationTick'],
                      checkpointSequence=checkpoint['checkpointSequence'], sql=sql_summary())
                crashed_at = time.monotonic()
                server.kill()
                assert server.process.returncode == -9
                server = Player(c, 'server', 'server-2', extra=extra, env_overrides=env)
                restored = server.wait(lambda x: x.get('generation') == saved['generation']+1 and
                    x.get('heroes') == 1 and x.get('paused') == 1, 60)
                assert restored['runId'] == saved['runId'] and restored['tick'] == checkpoint['simulationTick']
                server.send('capture')
                recovered = json.loads(Path(str(server.output)+'.checkpoint.json').read_text())
                (c.directory/'restored-checkpoint.json').write_text(json.dumps(recovered))
                for key in ('wood','Stone','Iron','randomState','totalSpawned','hero','matchStatistics','Projectiles','ResourcePickups'):
                    assert recovered[key] == checkpoint[key], key
                # Archetype/chunk iteration order is not a persistent identity.
                for key in ('targets','structures'):
                    expected = {x['stableId']:x for x in checkpoint[key]}
                    restored_actors = {x['stableId']:x for x in recovered[key]}
                    assert len(expected) == len(checkpoint[key])
                    assert len(restored_actors) == len(recovered[key])
                    assert restored_actors == expected, key
                # Wait for the actual transport timeout; Retry uses the existing presenter / grant flow.
                client.wait(lambda x: x.get('connection') == 'Lost', 60)
                client.send('retry')
                rejoined = client.wait(lambda x: x.get('connection') == 'InGame' and x.get('heroes') == 1, 60)
                after = c.db()
                assert rejoined['runId'] == saved['runId'] and after['runs'] == saved['runs']
                assert sql_summary()['energyCharges'] == charges_before
                assert after['results'] == saved['results']
                assert after['xpTransactions'] == saved['xpTransactions']
                after.pop('checkpoint')
                server.send('load512')
                client.send('unpause')
                resumed = server.wait(lambda x: x.get('paused') == 0 and x.get('eligibleConnections') == 1)
                event('recovered', state=resumed, client=rejoined, seconds=time.monotonic()-crashed_at,
                    lostSimulationSeconds=(before_crash['tick']-checkpoint['simulationTick'])/60,
                    checkpointAgeAtKillSeconds=before_crash['simulationSeconds']-checkpoint['simulationSeconds'], db=after)
                crash_done = True
            time.sleep(.5)
        assert crash_done
        final = sql_summary()
        with c.database.transaction() as session:
            operations = list(session.scalars(select(RunResultOperation)))
            completed = [r for r in final['runs'] if r['status'] == 'completed']
            assert len(operations) == len(completed)
            assert len({o.run_id for o in operations}) == len(operations)
            assert final['energyCharges'] == len(final['runs'])
            assert final['energyCharged'] == 5*len(final['runs'])
        accepted = [x for x in c.http_events if x['path'].endswith('/checkpoint') and x['status'] == 200]
        heartbeats = [x for x in c.http_events if x['path'].endswith('/heartbeat') and x['status'] == 200]
        # Endpoint spelling is retained in raw HTTP data and must really be observed.
        assert accepted, sorted({x['path'] for x in c.http_events})
        assert heartbeats
        assert max(x['state'].get('livingEnemies',0) for x in samples) == 512
        summary = dict(requestedSeconds=duration, requestedCrashAfterSeconds=crash_after, measuredSeconds=time.monotonic()-measured_started,
            totalSeconds=time.monotonic()-started, crashRecovered=crash_done,
            completedMatches=len(completed), sql=final, checkpointsAccepted=len(accepted),
            heartbeatsAccepted=len(heartbeats), maxLivingEnemies=max(x['state'].get('livingEnemies',0) for x in samples),
            initialWallet=c.initial_balance, finalWallet=c.wallet.balance(c.account)['balance'])
        paths['summary'].write_text(json.dumps(summary, indent=2))
        event('finished', **summary)
    finally:
        save()
