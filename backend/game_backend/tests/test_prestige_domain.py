from copy import deepcopy
import json
from pathlib import Path
import pytest
from vatican_backend.domain.prestige import PrestigeRules,bonuses


def rules():return json.loads((Path(__file__).parents[1]/'prestige-example.json').read_text())


@pytest.mark.parametrize('field,value',[('schemaVersion',True),('schemaVersion',2),('rulesetId',''),('upgrades',[]),('upgrades',None)])
def test_invalid_rule_envelope_is_rejected(field,value):
    with pytest.raises(ValueError):PrestigeRules({**rules(),field:value})


@pytest.mark.parametrize('value',['NaN','Infinity','0','-0.1','5.000001','0.1234567',0.1,True,None])
def test_invalid_percentage_is_rejected(value):
    body=rules();body['upgrades'][0]['levels'][0]['modifierValue']=value
    with pytest.raises(ValueError):PrestigeRules(body)


@pytest.mark.parametrize('patch',[{'target':'TREBUCHET'},{'stat':'unknown'},{'target':'WALL','stat':'MOVE_SPEED'},
    {'code':''},{'extra':1}])
def test_unsupported_bonus_never_reaches_publication(patch):
    body=rules();body['upgrades'][0].update(patch)
    with pytest.raises(ValueError):PrestigeRules(body)


@pytest.mark.parametrize('patch',[{'cost':0},{'cost':True},{'cost':2**63},{'level':2},{'level':True},{'modifierValue':'0.1'}])
def test_invalid_level_sequence_or_price_is_rejected(patch):
    body=rules();body['upgrades'][0]['levels'][0].update(patch)
    with pytest.raises(ValueError):PrestigeRules(body)


def test_duplicate_pair_or_code_is_rejected_and_snapshot_owns_copies():
    body=rules();before=deepcopy(body);parsed=PrestigeRules(body)
    body['upgrades'][0]['levels'][0]['cost']=1
    snapshot=parsed.snapshot();snapshot['upgrades'][0]['levels'][0]['cost']=2
    assert parsed.snapshot()['upgrades'][0]['levels'][0]['cost']==100
    for duplicate in (before['upgrades'][0],{**before['upgrades'][0],'code':'another'}):
        with pytest.raises(ValueError):PrestigeRules({**before,'upgrades':before['upgrades']+[duplicate]})


def test_bonus_is_latest_cumulative_value_not_sum_of_prior_levels():
    row={'code':'hero.damage','target':'HERO','stat':'ATTACK_DAMAGE','level':2,'modifierValue':'0.100000'}
    assert bonuses([row])['modifiers']==[{'target':'HERO','stat':'ATTACK_DAMAGE','multiplier':'1.100000'}]
    with pytest.raises(ValueError):bonuses([row,row])
