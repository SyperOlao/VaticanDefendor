from dataclasses import replace
from uuid import uuid4
import pytest
from vatican_backend.domain.progression import (ProgressionRules, XPRule, LevelDefinition, RunStats,
    MAX_XP, MAX_COUNT, calculate_xp, advance_level)


def policy(rules=None,levels=None):
    return ProgressionRules('xp-v1',rules or (
        XPRule('HERO_KILL','*',5,50), XPRule('TOWER_KILL','*',2,20),
        XPRule('BUILDING_UPGRADE','*',10,40), XPRule('RUN_VICTORY','*',20,20)),
        levels or (LevelDefinition(1,0),LevelDefinition(2,25),LevelDefinition(3,60),LevelDefinition(4,100)))


def test_xp_sources_are_calculated_separately_and_level_jumps_are_supported():
    xp=calculate_xp(policy(),RunStats((('orc',4),),(('orc',5),),(('church',3),)),True)
    assert xp.response()=={'heroKillXp':20,'towerKillXp':10,'tycoonXp':30,'victoryXp':20}
    assert xp.total_xp==80
    assert advance_level(policy(),80,1)==3


def test_wildcard_is_a_fallback_with_one_shared_cap_not_an_additional_reward():
    rules=policy((XPRule('HERO_KILL','boss',20,40),XPRule('HERO_KILL','*',1,10)))
    xp=calculate_xp(rules,RunStats((('boss',5),('orc',100),('skeleton',20))),False)
    assert xp.hero_kill_xp==50
    assert xp.total_xp==50


def test_defeat_keeps_combat_xp_without_victory_xp():
    assert calculate_xp(policy(),RunStats((('orc',2),)),False).total_xp==10
    assert calculate_xp(policy(),RunStats(),False).total_xp==0


def test_unknown_targets_without_wildcard_award_zero():
    rules=policy((XPRule('HERO_KILL','boss',20,40),))
    assert calculate_xp(rules,RunStats((('unknown',100),)),True).total_xp==0


def test_per_run_caps_apply_before_integer_overflow_can_reach_storage():
    rules=policy((XPRule('HERO_KILL','*',MAX_COUNT,10),))
    assert calculate_xp(rules,RunStats((('a',MAX_COUNT),('b',MAX_COUNT))),False).total_xp==10


def test_maximum_level_does_not_stop_total_xp_and_policy_changes_cannot_lower_level():
    assert advance_level(policy(),500,1)==4
    assert advance_level(policy(),0,10)==10


def test_snapshots_have_deterministic_order_and_hashes():
    one=policy()
    two=replace(one,rules=tuple(reversed(one.rules)))
    assert one.snapshot()==two.snapshot()
    assert one.digest()==two.digest()
    assert ProgressionRules.from_snapshot(one.snapshot()).snapshot()==one.snapshot()
    assert RunStats((('z',1),('a',2))).snapshot()==RunStats((('a',2),('z',1))).snapshot()


@pytest.mark.parametrize('count',[-1,True,1.5,'2',MAX_COUNT+1])
def test_invalid_counters_are_rejected(count):
    with pytest.raises(ValueError):
        calculate_xp(policy(),RunStats((('orc',count),)),True)


@pytest.mark.parametrize('entries',[(('a',1),('a',2)),(('*',2),),(('secret target',1),),tuple((str(i),1) for i in range(257))])
def test_duplicate_or_unbounded_targets_are_rejected(entries):
    with pytest.raises(ValueError):
        RunStats(entries).validate()


@pytest.mark.parametrize('rules',[(XPRule('UNKNOWN','*',1,10),),(XPRule('RUN_VICTORY','church',1,10),),
    (XPRule('HERO_KILL','*',-1,10),),(XPRule('HERO_KILL','*',True,10),),
    (XPRule('HERO_KILL','*',1,MAX_XP),XPRule('TOWER_KILL','*',1,1)),
    (XPRule('HERO_KILL','*',1,10),XPRule('HERO_KILL','*',2,10))])
def test_invalid_or_ambiguous_reward_rules_are_rejected(rules):
    with pytest.raises(ValueError):
        policy(rules)


@pytest.mark.parametrize('levels',[(LevelDefinition(1,1),),(LevelDefinition(1,0,uuid4()),),
    (LevelDefinition(1,0),LevelDefinition(3,5)),(LevelDefinition(1,0),LevelDefinition(2,0)),
    (LevelDefinition(1,0),LevelDefinition(2,True))])
def test_invalid_level_thresholds_are_rejected(levels):
    with pytest.raises(ValueError):
        policy(levels=levels)
