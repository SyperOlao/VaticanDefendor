"""Deterministic energy calculation; timestamps and effects come from the server."""
import re
from dataclasses import dataclass, replace
from datetime import datetime, timezone
from uuid import UUID
from decimal import Decimal, ROUND_DOWN, localcontext


MAX_BALANCE = 9_223_372_036_854_775_807
FRACTION_SCALE = Decimal("0.000000001")


@dataclass(frozen=True)
class EnergyRules:
    ruleset_id: str
    max_energy: int
    regen_interval_ms: int
    energy_per_tick: int
    run_cost: int

    def __post_init__(self):
        if (not isinstance(self.ruleset_id, str) or re.fullmatch(r"[A-Za-z0-9_.-]{1,128}", self.ruleset_id) is None
                or any(type(n) is not int for n in (self.max_energy, self.regen_interval_ms, self.energy_per_tick, self.run_cost))
                or not 1 <= self.max_energy <= 2_147_483_647
                or not 1 <= self.regen_interval_ms <= MAX_BALANCE
                or not 1 <= self.energy_per_tick <= 2_147_483_647
                or not 0 <= self.run_cost <= self.max_energy):
            raise ValueError("Invalid energy rules")


@dataclass(frozen=True)
class EnergyState:
    account_id: UUID
    ruleset_id: str
    current_energy: int
    last_calculated_at: datetime
    regen_progress_ms: int = 0
    regen_fraction_ms: Decimal = Decimal(0)
    state_version: int = 1


@dataclass(frozen=True)
class RegenEffect:
    starts_at: datetime
    expires_at: datetime
    rate_bonus: Decimal


def _aware(value: datetime) -> None:
    if value.tzinfo is None or value.utcoffset() is None:
        raise ValueError("Energy timestamps must include a timezone")


def _elapsed_ms(start: datetime, end: datetime) -> Decimal:
    elapsed = end - start
    micros = (elapsed.days * 86400 + elapsed.seconds) * 1_000_000 + elapsed.microseconds
    return Decimal(micros) / 1000


def regenerate(state: EnergyState, rules: EnergyRules, at: datetime, effects: list[RegenEffect]) -> EnergyState:
    _aware(at)
    _aware(state.last_calculated_at)
    state = replace(state, last_calculated_at=state.last_calculated_at.astimezone(timezone.utc))
    at = at.astimezone(timezone.utc)
    if state.ruleset_id != rules.ruleset_id:
        raise ValueError("Energy state and rules do not match")
    if not 0 <= state.current_energy <= MAX_BALANCE or not 0 <= state.regen_progress_ms < rules.regen_interval_ms:
        raise ValueError("Invalid energy state")
    if not Decimal(0) <= state.regen_fraction_ms < 1:
        raise ValueError("Invalid fractional tick progress")
    # Clock corrections must neither rewind the watermark nor grant the same interval twice.
    at = max(at, state.last_calculated_at)
    events = {state.last_calculated_at: Decimal(0), at: Decimal(0)}
    for effect in effects:
        _aware(effect.starts_at)
        _aware(effect.expires_at)
        if not effect.rate_bonus.is_finite() or effect.expires_at <= effect.starts_at:
            raise ValueError("Invalid energy effect")
        start = max(state.last_calculated_at, effect.starts_at.astimezone(timezone.utc))
        end = min(at, effect.expires_at.astimezone(timezone.utc))
        if start < end:
            events[start] = events.get(start, Decimal(0)) + effect.rate_bonus
            events[end] = events.get(end, Decimal(0)) - effect.rate_bonus
    energy = state.current_energy
    with localcontext() as context:
        context.prec = 80
        progress = Decimal(state.regen_progress_ms) + state.regen_fraction_ms
        bonus, cursor = Decimal(0), state.last_calculated_at
        for boundary, delta in sorted(events.items()):
            if energy >= rules.max_energy:
                # Paid energy above the cap survives; time spent full cannot be banked.
                progress = Decimal(0)
            else:
                progress += _elapsed_ms(cursor, boundary) * max(Decimal(0), 1 + bonus)
                ticks = int(progress // rules.regen_interval_ms)
                progress %= rules.regen_interval_ms
                energy = min(rules.max_energy, energy + ticks * rules.energy_per_tick)
                if energy == rules.max_energy:
                    progress = Decimal(0)
            bonus += delta
            cursor = boundary
        whole = int(progress)
        return replace(state, current_energy=energy, last_calculated_at=at,
                       regen_progress_ms=whole, regen_fraction_ms=progress - whole)


def switch_rules(state: EnergyState, old: EnergyRules, new: EnergyRules) -> EnergyState:
    """Settle with old rules first; carry the balance and the completed fraction of a tick."""
    if old.ruleset_id != state.ruleset_id:
        raise ValueError("Energy state and rules do not match")
    with localcontext() as context:
        context.prec = 80
        progress = ((Decimal(state.regen_progress_ms) + state.regen_fraction_ms)
                    * new.regen_interval_ms / old.regen_interval_ms).quantize(FRACTION_SCALE, rounding=ROUND_DOWN)
        if state.current_energy >= new.max_energy:
            progress = Decimal(0)
        whole = int(progress)
        return replace(state, ruleset_id=new.ruleset_id, regen_progress_ms=whole,
                       regen_fraction_ms=progress - whole)
