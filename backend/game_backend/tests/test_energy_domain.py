from dataclasses import replace
from datetime import datetime, timedelta, timezone
from decimal import Decimal
from random import Random
from uuid import uuid4
from zoneinfo import ZoneInfo
import pytest
from vatican_backend.domain.energy import EnergyRules, EnergyState, RegenEffect, next_regen_in_ms, regenerate, switch_rules

T0 = datetime(2026, 11, 17, tzinfo=timezone.utc)
RULES = EnergyRules('energy-v1', 100, 60_000, 2, 5)


def state(energy=0, progress=0, fraction=Decimal(0)):
    return EnergyState(uuid4(), RULES.ruleset_id, energy, T0, progress, fraction)


def test_regeneration_preserves_partial_ticks_without_rounding_loss():
    result = regenerate(state(5, 59_999, Decimal('.999999999')), RULES, T0 + timedelta(microseconds=1), [])
    assert result.current_energy == 7
    assert result.regen_progress_ms == 0
    assert result.regen_fraction_ms == Decimal('.000999999')


@pytest.mark.parametrize('balance', [100, 200, 9_223_372_036_854_775_807])
def test_full_and_overcap_balances_survive_and_do_not_bank_time(balance):
    result = regenerate(state(balance, 500), RULES, T0 + timedelta(days=3650), [])
    assert result.current_energy == balance
    assert result.regen_progress_ms == 0
    spent = replace(result, current_energy=95)
    assert regenerate(spent, RULES, result.last_calculated_at, []).current_energy == 95


def test_regeneration_is_bounded_for_long_offline_periods():
    result = regenerate(state(), RULES, T0 + timedelta(days=3650), [])
    assert result.current_energy == 100
    assert result.regen_progress_ms == 0


def test_effect_boundaries_and_additive_stacking_are_applied_only_during_their_lifetimes():
    effects = [RegenEffect(T0 + timedelta(seconds=10), T0 + timedelta(seconds=30), Decimal(1)),
               RegenEffect(T0 + timedelta(seconds=20), T0 + timedelta(seconds=40), Decimal('.5'))]
    # Base 40s + 20s from the first bonus + 10s from the second = 70s of regeneration work.
    result = regenerate(state(), RULES, T0 + timedelta(seconds=40), effects)
    assert result.current_energy == 2
    assert result.regen_progress_ms == 10_000


def test_next_tick_counts_overlapping_effects_and_rounds_up_partial_milliseconds():
    effects = [RegenEffect(T0, T0 + timedelta(seconds=10), Decimal('.5')),
               RegenEffect(T0, T0 + timedelta(seconds=30), Decimal('.5'))]
    before = state(5, 20_000, Decimal('.125'))
    delay = next_regen_in_ms(before, RULES, T0, effects)
    assert delay == 23_334
    assert regenerate(before, RULES, T0 + timedelta(milliseconds=delay - 1), effects).current_energy == 5
    assert regenerate(before, RULES, T0 + timedelta(milliseconds=delay), effects).current_energy == 7


def test_next_tick_waits_for_server_clock_to_reach_the_watermark():
    assert next_regen_in_ms(state(progress=40_000), RULES, T0 - timedelta(seconds=15), []) == 35_000


def test_a_debuff_can_pause_regeneration_without_erasing_completed_work():
    effect = RegenEffect(T0, T0 + timedelta(seconds=60), Decimal(-2))
    result = regenerate(state(0, 30_000), RULES, T0 + timedelta(seconds=90), [effect])
    assert result.current_energy == 2
    assert result.regen_progress_ms == 0


def test_repeated_fractional_reads_equal_one_full_calculation():
    rules = EnergyRules('energy-v1',100,100,1,5)
    effects = [RegenEffect(T0,T0 + timedelta(seconds=1),Decimal('.333333'))]
    before=state()
    one=regenerate(before,rules,T0 + timedelta(seconds=1),effects)
    many=before
    for step in range(1,1001):
        many=regenerate(many,rules,T0 + timedelta(milliseconds=step),effects)
    assert many==one
    assert one.current_energy==13
    assert one.regen_progress_ms==33
    assert one.regen_fraction_ms==Decimal('.333')


def test_clock_rollback_does_not_move_watermark_or_credit_the_same_time_twice():
    first=regenerate(state(),RULES,T0 + timedelta(seconds=60),[])
    back=regenerate(first,RULES,T0 + timedelta(seconds=30),[])
    assert back==first
    assert regenerate(back,RULES,T0 + timedelta(seconds=90),[]).current_energy==2


def test_dst_and_display_timezone_do_not_change_elapsed_time():
    zone=ZoneInfo('America/New_York')
    start=datetime(2026,11,1,1,30,tzinfo=zone,fold=0)
    end=datetime(2026,11,1,1,30,tzinfo=zone,fold=1)
    rules=EnergyRules('energy-v1',100,60000,1,5)
    before=replace(state(),last_calculated_at=start)
    result=regenerate(before,rules,end,[])
    assert result.current_energy==60
    assert result.last_calculated_at==end.astimezone(timezone.utc)


def test_rules_change_keeps_balance_and_completed_tick_fraction():
    new=EnergyRules('energy-v2',50,120_000,1,10)
    result=switch_rules(state(20,30_000),RULES,new)
    assert result.current_energy==20
    assert result.regen_progress_ms==60_000
    assert result.ruleset_id=='energy-v2'
    assert switch_rules(state(80,30_000),RULES,new).current_energy==80
    assert switch_rules(state(80,30_000),RULES,new).regen_progress_ms==0


@pytest.mark.parametrize('updates',[{'max_energy':0},{'regen_interval_ms':0},{'energy_per_tick':0},
    {'run_cost':101},{'run_cost':-1},{'run_cost':True}])
def test_invalid_balance_rules_are_rejected(updates):
    with pytest.raises(ValueError):
        replace(RULES,**updates)


def test_randomly_partitioned_reads_preserve_offline_calculation():
    random=Random(792451)
    rules=EnergyRules('energy-v1',100,15_000,1,5)
    effects=[RegenEffect(T0 + timedelta(seconds=2),T0 + timedelta(seconds=31),Decimal('.123456')),
             RegenEffect(T0 + timedelta(seconds=19),T0 + timedelta(seconds=40),Decimal('-.5'))]
    before=state(20,789,Decimal('.123456789'))
    expected=regenerate(before,rules,T0 + timedelta(seconds=60),effects)
    for _ in range(20):
        result=before
        cuts=sorted(set(random.randrange(1,60_000_000) for _ in range(30)))+[60_000_000]
        for micros in cuts:
            result=regenerate(result,rules,T0 + timedelta(microseconds=micros),effects)
        assert result==expected
