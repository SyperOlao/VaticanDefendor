from dataclasses import dataclass
from datetime import datetime
from decimal import Decimal
from typing import Protocol
from uuid import UUID
from ..domain.energy import EnergyRules, EnergyState, RegenEffect


@dataclass(frozen=True)
class EnergyCharge:
    run_id: UUID
    account_id: UUID
    ruleset_id: str
    cost: int
    refunded_at: datetime | None


@dataclass(frozen=True)
class GrantedEffect:
    id: UUID
    account_id: UUID
    effect_type: str
    modifier_value: Decimal
    source_key: str
    starts_at: datetime
    expires_at: datetime


class EnergyRepository(Protocol):
    def configuration(self, ruleset_id: str, published_only: bool = False) -> EnergyRules | None: ...
    def configure(self, rules: EnergyRules) -> bool: ...
    def state(self, account_id: UUID) -> EnergyState | None: ...
    def save(self, state: EnergyState) -> None: ...
    def effects(self, account_id: UUID, after: datetime, before: datetime | None = None) -> list[RegenEffect]: ...
    def record_charge(self, run_id: UUID, account_id: UUID, rules: EnergyRules) -> None: ...
    def charge(self, run_id: UUID) -> EnergyCharge | None: ...
    def mark_refunded(self, run_id: UUID, at: datetime) -> None: ...
    def effect(self, account_id: UUID, source_key: str) -> GrantedEffect | None: ...
    def add_effect(self, account_id: UUID, source_key: str, bonus: Decimal,
                   starts_at: datetime, expires_at: datetime) -> GrantedEffect: ...
