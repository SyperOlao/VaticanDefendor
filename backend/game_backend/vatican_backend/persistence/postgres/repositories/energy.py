from uuid import uuid4
from sqlalchemy import select
from sqlalchemy.dialects.postgresql import insert
from ....application.catalog import EffectType, RulesetStatus
from ....application.energy_ports import EnergyCharge, GrantedEffect
from ....domain.energy import EnergyRules, EnergyState, RegenEffect
from ..tables import EnergyConfig, PlayerEnergy, PlayerEffect, Ruleset, RunEnergyCharge


class EnergyRepository:
    def __init__(self, session):
        self.session = session

    def configuration(self, ruleset_id, published_only=False):
        query = select(EnergyConfig).join(Ruleset, Ruleset.id == EnergyConfig.ruleset_id).where(
            EnergyConfig.ruleset_id == ruleset_id, EnergyConfig.run_cost.is_not(None))
        if published_only:
            query = query.where(Ruleset.status == RulesetStatus.PUBLISHED)
        row = self.session.scalar(query)
        return EnergyRules(row.ruleset_id, row.max_energy, row.regen_interval_ms, row.energy_per_tick, row.run_cost) if row else None

    def configure(self, rules):
        # Serialize first publication as well as later attempts to change the same version.
        from sqlalchemy import func
        self.session.execute(select(func.pg_advisory_xact_lock(func.hashtextextended("energy:" + rules.ruleset_id, 0))))
        existing = self.session.get(EnergyConfig, rules.ruleset_id)
        if existing is not None:
            if self.session.get(Ruleset, rules.ruleset_id).status != RulesetStatus.PUBLISHED:
                return False
            if existing.run_cost is None:
                # A legacy row can be finalized only with the exact same regeneration parameters.
                if (existing.max_energy, existing.regen_interval_ms, existing.energy_per_tick) != (
                        rules.max_energy, rules.regen_interval_ms, rules.energy_per_tick):
                    return False
                existing.run_cost = rules.run_cost
                return True
            return self.configuration(rules.ruleset_id) == rules
        self.session.execute(insert(Ruleset).values(id=rules.ruleset_id, code=rules.ruleset_id,
            version=1, status=RulesetStatus.PUBLISHED).on_conflict_do_nothing(index_elements=[Ruleset.id]))
        ruleset = self.session.get(Ruleset, rules.ruleset_id)
        if ruleset.status != RulesetStatus.PUBLISHED:
            return False
        self.session.add(EnergyConfig(ruleset_id=rules.ruleset_id, max_energy=rules.max_energy,
            regen_interval_ms=rules.regen_interval_ms, energy_per_tick=rules.energy_per_tick, run_cost=rules.run_cost))
        return True

    def state(self, account_id):
        row = self.session.get(PlayerEnergy, account_id)
        if row is None:
            return None
        return EnergyState(row.account_id, row.ruleset_id, row.current_energy, row.last_calculated_at,
                           row.regen_progress_ms, row.regen_fraction_ms, row.state_version)

    def save(self, state):
        values = {name: getattr(state, name) for name in state.__dataclass_fields__}
        statement = insert(PlayerEnergy).values(**values)
        self.session.execute(statement.on_conflict_do_update(index_elements=[PlayerEnergy.account_id],
            set_={name: getattr(statement.excluded, name) for name in values if name != "account_id"}))

    def effects(self, account_id, after, before=None):
        query = select(PlayerEffect).where(PlayerEffect.account_id == account_id,
            PlayerEffect.effect_type == EffectType.ENERGY_REGEN_RATE_BONUS,
            PlayerEffect.expires_at > after)
        if before is not None:
            query = query.where(PlayerEffect.starts_at < before)
        return [RegenEffect(row.starts_at, row.expires_at, row.modifier_value) for row in self.session.scalars(query)]

    def record_charge(self, run_id, account_id, rules):
        self.session.add(RunEnergyCharge(run_id=run_id, account_id=account_id, ruleset_id=rules.ruleset_id, cost=rules.run_cost))

    def charge(self, run_id):
        row = self.session.get(RunEnergyCharge, run_id, with_for_update=True)
        return EnergyCharge(row.run_id, row.account_id, row.ruleset_id, row.cost, row.refunded_at) if row else None

    def mark_refunded(self, run_id, at):
        self.session.get(RunEnergyCharge, run_id).refunded_at = at

    @staticmethod
    def _effect(row):
        return GrantedEffect(row.id, row.account_id, row.effect_type, row.modifier_value,
                             row.source_key, row.starts_at, row.expires_at)

    def effect(self, account_id, source_key):
        row = self.session.scalar(select(PlayerEffect).where(PlayerEffect.account_id == account_id, PlayerEffect.source_key == source_key))
        return self._effect(row) if row else None

    def add_effect(self, account_id, source_key, bonus, starts_at, expires_at):
        row = PlayerEffect(id=uuid4(), account_id=account_id, source_key=source_key,
            effect_type=EffectType.ENERGY_REGEN_RATE_BONUS, modifier_value=bonus,
            starts_at=starts_at, expires_at=expires_at)
        self.session.add(row)
        return self._effect(row)
