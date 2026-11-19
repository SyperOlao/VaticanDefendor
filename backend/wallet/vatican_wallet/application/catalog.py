from enum import StrEnum

class CurrencyCode(StrEnum):
    PRESTIGE = "PRESTIGE"
    GEMS = "GEMS"

class SourceType(StrEnum):
    BOOTSTRAP = "BOOTSTRAP"
    RUN_REWARD = "RUN_REWARD"
    LEVEL_REWARD = "LEVEL_REWARD"
    CASE_REWARD = "CASE_REWARD"
    PRESTIGE_UPGRADE = "PRESTIGE_UPGRADE"
    PRESTIGE_UPGRADE_REFUND = "PRESTIGE_UPGRADE_REFUND"
    CASE_OPENING = "CASE_OPENING"
    PURCHASE = "PURCHASE"
    REFUND = "REFUND"


class EventName(StrEnum):
    PURCHASE_CHARGE = "wallet.purchase.charge"
    PURCHASE_REFUND = "wallet.purchase.refund"
    RUN_CURRENCY = "run.currency_reward"
    LEVEL_REWARD = "level.reward"
    CASE_REWARD = "case.reward"
    PRESTIGE_PAYMENT = "prestige-upgrade.payment"
    PRESTIGE_REFUND = "prestige-upgrade.refund"
    CASE_FUNDING = "case.payment"

class RewardType(StrEnum):
    CURRENCY = "CURRENCY"


class MessageStatus(StrEnum):
    APPLIED = "APPLIED"
