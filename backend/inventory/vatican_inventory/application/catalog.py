from enum import StrEnum

class EventName(StrEnum):
    RUN_REWARD = "run.weapon_reward"
    LEVEL_REWARD = "level.reward"
    CASE_REWARD = "case.reward"
    CASE_FUNDING = "case.consume"
    ITEM_CONSUMPTION = "inventory.item.consume"
    PURCHASE_GRANT = "inventory.weapon.grant"

class RewardType(StrEnum):
    WEAPON = "WEAPON"
    ITEM = "ITEM"
    CASE = "CASE"


class MessageStatus(StrEnum):
    APPLIED = "APPLIED"
    REJECTED = "REJECTED"


class Operation(StrEnum):
    PURCHASE = "inventory.purchase"
    LOADOUT = "inventory.loadout"
    ITEM_CONSUMPTION = "inventory.item.consume"


class OperationStatus(StrEnum):
    COMPLETED = "COMPLETED"
    REJECTED = "REJECTED"


CONSUMER = "rewards.v1"
