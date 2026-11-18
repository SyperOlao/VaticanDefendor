"""Versioned Dedicated Server telemetry; rewards still use the existing RunStats rules."""
import json
from .progression import RunStats, MAX_COUNT, MAX_TARGETS, TARGET, integer

MAX_VALUE = 9_223_372_036_854_775_807
COUNTERS = ('heroKills', 'towerKills', 'buildingsBuilt', 'buildingUpgrades')
VALUES = ('enemiesSpawned', 'enemiesKilled', 'otherKills', 'heroDamageMilliUnits', 'towerDamageMilliUnits')


def counters(value):
    if not isinstance(value, list) or len(value) > MAX_TARGETS:
        raise ValueError('Invalid match counters')
    result = {}
    for entry in value:
        if (not isinstance(entry, dict) or set(entry) != {'key', 'count'}
                or not isinstance(entry['key'], str) or TARGET.fullmatch(entry['key']) is None
                or entry['key'] in result or not integer(entry['count'], 0, MAX_COUNT)):
            raise ValueError('Invalid match counter')
        result[entry['key']] = entry['count']
    return dict(sorted(result.items()))


def resources(value):
    if not isinstance(value, dict) or set(value) != {'wood', 'stone', 'iron'}:
        raise ValueError('Invalid match resources')
    if any(not integer(x, 0, MAX_VALUE) for x in value.values()):
        raise ValueError('Invalid match resource amount')
    return dict(value)


def aggregate(value, extra=()):
    fields = set(COUNTERS + VALUES + ('resourcesProduced', 'resourcesSpent') + tuple(extra))
    if not isinstance(value, dict) or set(value) != fields:
        raise ValueError('Invalid match aggregate fields')
    result = {key: [{'key': k, 'count': n} for k, n in counters(value[key]).items()] for key in COUNTERS}
    for key in VALUES:
        if not integer(value[key], 0, MAX_COUNT if key in VALUES[:3] else MAX_VALUE):
            raise ValueError('Invalid match aggregate value')
        result[key] = value[key]
    result.update({key: resources(value[key]) for key in ('resourcesProduced', 'resourcesSpent')})
    killed = sum(x['count'] for key in ('heroKills', 'towerKills') for x in result[key]) + result['otherKills']
    if killed != result['enemiesKilled'] or killed > result['enemiesSpawned']:
        raise ValueError('Match kills do not reconcile')
    return result


def validate_snapshot(value):
    if len(json.dumps(value, separators=(',', ':')).encode()) > 200_000:
        raise ValueError('Match snapshot exceeds analytics payload budget')
    extra = ('schemaVersion', 'complete', 'durationMs', 'simulationTick', 'buildings', 'waves')
    result = aggregate(value, extra)
    if value['schemaVersion'] != 1 or type(value['schemaVersion']) is not int or value['complete'] is not True:
        raise ValueError('Incomplete or unsupported match statistics')
    if not integer(value['durationMs'], 0, MAX_VALUE) or not integer(value['simulationTick'], 0, MAX_VALUE):
        raise ValueError('Invalid match clock')
    if not isinstance(value['waves'], list) or len(value['waves']) > 1000:
        raise ValueError('Invalid match waves')
    waves = []
    seen = set()
    for item in value['waves']:
        wave = aggregate(item, ('waveNumber',))
        number = item['waveNumber']
        if not integer(number, 1, 1000) or number in seen:
            raise ValueError('Invalid match wave number')
        seen.add(number); wave['waveNumber'] = number; waves.append(wave)
    for key in VALUES:
        if sum(w[key] for w in waves) != result[key]:
            raise ValueError('Wave totals do not reconcile')
    for key in COUNTERS:
        merged = {}
        for wave in waves:
            for entry in wave[key]:
                merged[entry['key']] = merged.get(entry['key'], 0) + entry['count']
        if {k: v for k, v in merged.items() if v} != {k: v for k, v in counters(result[key]).items() if v}:
            raise ValueError('Wave counters do not reconcile')
    for key in ('resourcesProduced', 'resourcesSpent'):
        if any(sum(w[key][kind] for w in waves) != result[key][kind] for kind in ('wood', 'stone', 'iron')):
            raise ValueError('Wave resources do not reconcile')
    result.update(schemaVersion=1, complete=True, durationMs=value['durationMs'], simulationTick=value['simulationTick'],
                  buildings=[{'key': k, 'count': n} for k, n in counters(value['buildings']).items()],
                  waves=sorted(waves, key=lambda w: w['waveNumber']))
    stats = RunStats(tuple(counters(result['heroKills']).items()), tuple(counters(result['towerKills']).items()),
                     tuple(counters(result['buildingUpgrades']).items()))
    # The analytics envelope repeats the three XP counters; reserve room for identity/progression.
    if len(json.dumps({'matchResultSnapshot': result, 'stats': stats.snapshot()}, separators=(',', ':')).encode()) > 240_000:
        raise ValueError('Match snapshot and XP counters exceed analytics payload budget')
    return result, stats
