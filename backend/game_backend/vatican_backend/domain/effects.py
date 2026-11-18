"""Server-owned parameters for timed regeneration effects."""
from dataclasses import dataclass
from decimal import Decimal
from enum import StrEnum


class EffectType(StrEnum):
    ENERGY_REGEN_RATE_BONUS = 'ENERGY_REGEN_RATE_BONUS'


def validate_regen_parameters(bonus, duration_seconds):
    try:
        bonus = Decimal(bonus)
        if (not bonus.is_finite() or abs(bonus) >= Decimal('1000000000000')
                or bonus != bonus.quantize(Decimal('0.000001'))
                or type(duration_seconds) is not int or not 1 <= duration_seconds <= 31_536_000):
            raise ValueError()
    except (ValueError, TypeError, ArithmeticError):
        raise ValueError('Invalid regeneration effect') from None
    return bonus


@dataclass(frozen=True)
class GiftEffect:
    modifier_value: Decimal
    duration_seconds: int

    def __post_init__(self):
        if not isinstance(self.modifier_value, Decimal) or validate_regen_parameters(
                self.modifier_value, self.duration_seconds) <= 0:
            raise ValueError('Gift regeneration bonus must be a positive decimal')

    @classmethod
    def parse(cls, value):
        if (not isinstance(value, dict) or set(value) != {'modifierValue', 'durationSeconds'}
                or not isinstance(value['modifierValue'], str)):
            raise ValueError('Invalid gift effect parameters')
        bonus = validate_regen_parameters(value['modifierValue'], value['durationSeconds'])
        return cls(bonus, value['durationSeconds'])

    def snapshot(self):
        return {'modifierValue': format(self.modifier_value, '.6f'), 'durationSeconds': self.duration_seconds}
