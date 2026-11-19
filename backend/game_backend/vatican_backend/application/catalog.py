from enum import StrEnum
from ..domain.progression import XPSource
from ..domain.effects import EffectType
from ..domain.rating import RATING_EVENT
from ..domain.analytics import ANALYTICS_EVENT


class RuntimeStatus(StrEnum):
    READY = "ready"
    PENDING = "pending"
    ASSIGNED = "assigned"
    OCCUPIED = "occupied"
    UNAVAILABLE = "unavailable"
    COMPLETED = "completed"
    FAILED = "failed"


class SimulationMode(StrEnum):
    LOCAL = "LOCAL"
    DEDICATED = "DEDICATED"


class RulesetStatus(StrEnum):
    PUBLISHED = "PUBLISHED"


class RewardStatus(StrEnum):
    PENDING = "PENDING"
    COMPLETED = "COMPLETED"
    FAILED = "FAILED"


class Outcome(StrEnum):
    VICTORY = "victory"
    DEFEAT = "defeat"


class RewardEvent(StrEnum):
    ANALYTICS = ANALYTICS_EVENT
    RATING = RATING_EVENT
    CURRENCY = "run.currency_reward"
    WEAPON = "run.weapon_reward"


class RewardDestination(StrEnum):
    ANALYTICS = "analytics"
    RATING = "rating"
    WALLET = "wallet"
    INVENTORY = "inventory"


class GameErrorCode(StrEnum):
    SESSION_EXPIRED = "SESSION_EXPIRED"
    DISPLAY_NAME_TAKEN = "DisplayNameTaken"
    INSUFFICIENT_ENERGY = "InsufficientEnergy"
    ENERGY_RULES_UNAVAILABLE = "EnergyRulesUnavailable"
    ASSIGNMENT_EXPIRED = "AssignmentExpired"
    PROGRESSION_RULES_UNAVAILABLE = "ProgressionRulesUnavailable"
    XP_CAPACITY_EXCEEDED = "XpCapacityExceeded"
    INVALID_REWARD_BUNDLE = "InvalidRewardBundle"
    PRESTIGE_PURCHASE_NOT_CREATED = "PrestigePurchaseNotCreated"
    PRESTIGE_RESET_NOT_CREATED = "PrestigeResetNotCreated"


class LevelClaimStatus(StrEnum):
    AVAILABLE = "AVAILABLE"
    PENDING = "PENDING"
    REVIEW = "REVIEW"
    CLAIMED = "CLAIMED"
