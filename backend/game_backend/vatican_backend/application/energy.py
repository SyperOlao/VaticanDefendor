from dataclasses import replace
from datetime import datetime, timedelta
from decimal import Decimal
from uuid import UUID
from ..domain.clock import Clock, UtcClock
from ..domain.energy import EnergyRules, EnergyState, MAX_BALANCE, regenerate, switch_rules
from ..domain.effects import validate_regen_parameters
from .catalog import EffectType, GameErrorCode
from .errors import ApplicationError
from .ports import UnitOfWork, UnitOfWorkFactory


def response(state: EnergyState, rules: EnergyRules) -> dict:
    return {"accountId": str(state.account_id), "rulesetId": state.ruleset_id,
            "currentEnergy": state.current_energy, "maxEnergy": rules.max_energy,
            "runCost": rules.run_cost, "regenIntervalMs": rules.regen_interval_ms,
            "energyPerTick": rules.energy_per_tick, "regenProgressMs": state.regen_progress_ms,
            "regenFractionMs": str(state.regen_fraction_ms), "stateVersion": state.state_version,
            "calculatedAt": state.last_calculated_at.isoformat()}


class EnergyService:
    """Called under the player's profile lock; never opens another transaction."""
    def __init__(self, ruleset_id: str):
        self.ruleset_id = ruleset_id

    def refresh(self, work: UnitOfWork, account_id: UUID, at: datetime, *,
                ruleset_id: str | None = None, published_only: bool = True) -> tuple[EnergyState, EnergyRules]:
        selected = ruleset_id or self.ruleset_id
        if not selected:
            raise ApplicationError(503, "Energy rules are not configured", code=GameErrorCode.ENERGY_RULES_UNAVAILABLE)
        rules = work.energy.configuration(selected, published_only=published_only)
        if rules is None:
            raise ApplicationError(503, "Published energy rules are unavailable", code=GameErrorCode.ENERGY_RULES_UNAVAILABLE)
        before = work.energy.state(account_id)
        if before is None:
            state = EnergyState(account_id, rules.ruleset_id, rules.max_energy, at)
        else:
            old = work.energy.configuration(before.ruleset_id)
            if old is None:
                raise ApplicationError(503, "Previous energy rules are unavailable", code=GameErrorCode.ENERGY_RULES_UNAVAILABLE)
            effects = work.energy.effects(account_id, before.last_calculated_at, max(at, before.last_calculated_at))
            state = regenerate(before, old, at, effects)
            if old.ruleset_id != rules.ruleset_id:
                state = switch_rules(state, old, rules)
            if state != before:
                state = replace(state, state_version=before.state_version + 1)
        if state != before:
            work.energy.save(state)
        return state, rules

    def spend(self, work: UnitOfWork, account_id: UUID, run_id: UUID, at: datetime) -> int:
        state, rules = self.refresh(work, account_id, at)
        if state.current_energy < rules.run_cost:
            raise ApplicationError(409, "Insufficient energy", code=GameErrorCode.INSUFFICIENT_ENERGY)
        work.energy.save(replace(state, current_energy=state.current_energy - rules.run_cost,
                                 state_version=state.state_version + 1))
        work.energy.record_charge(run_id, account_id, rules)
        return rules.run_cost

    def credit(self, work: UnitOfWork, account_id: UUID, amount: int, at: datetime) -> dict:
        if type(amount) is not int or not 1 <= amount <= MAX_BALANCE:
            raise ApplicationError(422, "Invalid energy reward amount")
        state, rules = self.refresh(work, account_id, at)
        if state.current_energy + amount > MAX_BALANCE:
            raise ApplicationError(409, "Energy capacity exceeded")
        updated = replace(state, current_energy=state.current_energy + amount, state_version=state.state_version + 1)
        if updated.current_energy >= rules.max_energy:
            updated = replace(updated, regen_progress_ms=0, regen_fraction_ms=Decimal(0))
        work.energy.save(updated)
        return response(updated, rules)

    def refund_unstarted(self, work: UnitOfWork, run, at: datetime) -> bool:
        charge = work.energy.charge(run.id)
        if run.started_at is not None or charge is None or charge.refunded_at is not None:
            return False
        if charge.account_id != run.account_id:
            raise ApplicationError(409, "Energy charge owner mismatch")
        before = work.energy.state(run.account_id)
        if before is None:
            raise ApplicationError(503, "Energy state is unavailable for refund")
        # Refund the recorded charge even if the operator changed or retired the active policy.
        state, _ = self.refresh(work, run.account_id, at, ruleset_id=before.ruleset_id, published_only=False)
        if state.current_energy + charge.cost > MAX_BALANCE:
            raise ApplicationError(409, "Energy capacity exceeded")
        work.energy.save(replace(state, current_energy=state.current_energy + charge.cost,
                                 state_version=state.state_version + 1))
        work.energy.mark_refunded(run.id, state.last_calculated_at)
        return True

    def grant_regen_effect(self, work: UnitOfWork, account_id: UUID, source_key: str,
                           bonus, duration_seconds: int, at: datetime):
        try:
            bonus = validate_regen_parameters(bonus, duration_seconds)
            if not isinstance(source_key, str) or not 1 <= len(source_key) <= 128:
                raise ValueError()
        except ValueError:
            raise ApplicationError(422, 'Invalid energy effect') from None
        prior = work.energy.effect(account_id, source_key)
        if prior:
            if (prior.effect_type != EffectType.ENERGY_REGEN_RATE_BONUS or prior.modifier_value != bonus
                    or prior.expires_at - prior.starts_at != timedelta(seconds=duration_seconds)):
                raise ApplicationError(409, 'Effect source payload changed')
            return prior
        state, _ = self.refresh(work, account_id, at)
        return work.energy.add_effect(account_id, source_key, bonus, state.last_calculated_at,
                                      state.last_calculated_at + timedelta(seconds=duration_seconds))


class EnergyUseCases:
    def __init__(self, uow: UnitOfWorkFactory, service: EnergyService, clock: Clock | None = None):
        self.uow, self.service, self.clock = uow, service, clock or UtcClock()

    @staticmethod
    def _lock(work, account_id):
        if work.profiles.get(account_id, lock=True) is None:
            raise ApplicationError(404, "Profile not found")

    def get(self, account_id):
        with self.uow() as work:
            self._lock(work, account_id)
            state, rules = self.service.refresh(work, account_id, self.clock.now())
            return response(state, rules)

    def grant_regen_effect(self, account_id, source_key, bonus, duration_seconds):
        if isinstance(source_key, str) and source_key.startswith(('level-reward:', 'case-reward:')):
            raise ApplicationError(422, 'Reserved reward source')
        with self.uow() as work:
            self._lock(work, account_id)
            return self.service.grant_regen_effect(work, account_id, source_key, bonus, duration_seconds, self.clock.now())


class ConfigureEnergy:
    def __init__(self, uow: UnitOfWorkFactory):
        self.uow = uow

    def execute(self, rules: EnergyRules):
        with self.uow() as work:
            if not work.energy.configure(rules):
                raise ApplicationError(409, "Energy rules are immutable; publish a new ruleset ID")
