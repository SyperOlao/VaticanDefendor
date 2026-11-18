from copy import deepcopy
import json
from pathlib import Path
from uuid import uuid4
import pytest
from vatican_backend.domain.local_runs import LocalPolicy,LocalResult,digest,start_digest
from vatican_backend.domain.progression import RunStats


def policy():return json.loads((Path(__file__).parents[1]/'local-run-example.json').read_text())


@pytest.mark.parametrize('change',[
    {'unknown':1},{'minDurationMs':True},{'submissionWindowSeconds':0},{'clockToleranceMs':60001},
    {'maxDurationMs':1},{'minVictoryDurationMs':999999999},{'victoryChurchLevel':1},
    {'prestigePerChurchLevel':-1},{'rewardWeaponConfigurationId':''},{'difficulty':'a'*33},
    {'maxEventsPerSecond':0},{'counterLimits':{'heroKills':{}}},
    {'counterLimits':{'heroKills':{'orc':True},'towerKills':{},'buildingUpgrades':{}}},
    {'counterLimits':{'heroKills':{'orc':-1},'towerKills':{},'buildingUpgrades':{}}},
])
def test_policy_rejects_ambiguous_or_unbounded_rules(change):
    with pytest.raises(ValueError):LocalPolicy({**policy(),**change})


def test_policy_owns_immutable_copies_and_hash_is_canonical():
    value=policy();p=LocalPolicy(value);saved=deepcopy(value)
    value['counterLimits']['heroKills']['orc']=0
    snapshot=p.snapshot();snapshot['counterLimits']['heroKills']['orc']=1
    p['counterLimits']['heroKills']['orc']=2
    assert p.snapshot()==saved
    assert digest({'a':1,'b':2})==digest({'b':2,'a':1})
    assert start_digest('one')!=start_digest('two')


def test_result_roundtrips_and_counter_order_preserves_identity():
    r=LocalResult(uuid4(),'policy','content','rules','map','normal','defeat',1,1000,
                  RunStats((('orc',2),('skeleton',1))))
    assert LocalResult.from_snapshot(r.snapshot())==r
    from dataclasses import replace
    assert digest(r.snapshot())==digest(replace(r,stats=RunStats((('skeleton',1),('orc',2)))).snapshot())
