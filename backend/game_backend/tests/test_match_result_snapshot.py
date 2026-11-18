from copy import deepcopy
import pytest
from vatican_backend.domain.match_results import validate_snapshot
from vatican_backend.domain.progression import RunStats
from .test_dedicated_match_result import snapshot


@pytest.mark.parametrize('mutate',[
    lambda x:x.update(schemaVersion=2),
    lambda x:x.update(schemaVersion=True),
    lambda x:x.update(complete=False),
    lambda x:x.update(xp=999),
    lambda x:x.update(durationMs=-1),
    lambda x:x['heroKills'].append(dict(x['heroKills'][0])),
    lambda x:x['waves'].append(deepcopy(x['waves'][0])),
    lambda x:x['waves'][0].update(waveNumber=0),
    lambda x:x['waves'][0].update(enemiesSpawned=99),
    lambda x:x['resourcesSpent'].update(wood=11),
    lambda x:x['waves'][0]['resourcesProduced'].update(iron=1),
    lambda x:x['heroKills'][0].update(count=True),
    lambda x:x['heroKills'][0].update(key='unknown/private/field'),
])
def test_invalid_or_incomplete_snapshot_is_rejected(mutate):
    value=snapshot(RunStats((('enemy.kind.0',2),),(('enemy.kind.0',1),),(('church',2),)))
    mutate(value)
    with pytest.raises(ValueError):validate_snapshot(value)


def test_counter_and_wave_order_have_one_canonical_identity():
    value=snapshot(RunStats((('b',1),('a',2)),( ('b',1),),(('church',2),)))
    normalized,stats=validate_snapshot(value)
    value['heroKills'].reverse();value['waves'][0]['heroKills'].reverse();value['buildings'].reverse()
    assert validate_snapshot(value)==(normalized,stats)
    assert stats.hero_kills==( ('a',2),('b',1))


def test_analytics_extension_accepts_same_snapshot_and_rejects_divergent_award_counters():
    from vatican_analytics.domain.events import parse_match_snapshot
    value=snapshot(RunStats((('enemy.kind.0',2),),(('enemy.kind.0',1),),(('church',2),)))
    canonical,stats=validate_snapshot(value)
    parse_match_snapshot(canonical,stats.snapshot())
    wrong=stats.snapshot();wrong['heroKills']['enemy.kind.0']=3
    with pytest.raises(ValueError):parse_match_snapshot(canonical,wrong)


def test_snapshot_budget_includes_duplicated_analytics_xp_counters():
    # Valid counters can fit the inner snapshot while overflowing the full event.
    keys=tuple((f'kind.{i:03d}.'+'x'*105,1) for i in range(256))
    value=snapshot(RunStats(keys,keys,keys[:128]))
    import json
    assert len(json.dumps(value,separators=(',',':')).encode())<200_000
    with pytest.raises(ValueError,match='XP counters exceed analytics'):
        validate_snapshot(value)
