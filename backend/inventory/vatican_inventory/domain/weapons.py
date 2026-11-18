from dataclasses import dataclass
import re
from uuid import UUID
from datetime import datetime


MAX_INTEGER = 2_147_483_647
STARTER_CONFIGURATION = "sword.common"
MAX_LOADOUT_SIZE = 3


def configuration_id(value: object) -> str:
    if not isinstance(value, str) or re.fullmatch(r"[A-Za-z0-9_.-]{1,80}", value) is None:
        raise ValueError("Invalid weapon configuration identifier")
    return value


@dataclass(frozen=True)
class WeaponProgression:
    experience_per_level: int
    maximum_level: int

    @classmethod
    def parse(cls, definition: dict) -> "WeaponProgression":
        xp, level = definition["ExperiencePerLevel"], definition["MaximumLevel"]
        if any(type(value) is not int or not 1 <= value <= MAX_INTEGER for value in (xp, level)):
            raise ValueError("Invalid weapon progression")
        return cls(xp, level)

    def advance(self, level: int, experience: int) -> tuple[int, int]:
        experience += self.experience_per_level
        if experience > MAX_INTEGER:
            raise ValueError("Weapon experience capacity exceeded")
        return max(level, min(self.maximum_level, 1 + experience // self.experience_per_level)), experience


@dataclass(frozen=True)
class Weapon:
    instance_id: UUID
    configuration_id: str
    level: int
    experience: int


@dataclass(frozen=True)
class WeaponEntry:
    weapon: Weapon
    created_at: datetime


@dataclass(frozen=True)
class Item:
    item_id: str
    quantity: int


@dataclass(frozen=True)
class InventoryState:
    revision: int


def definition_map(values: object) -> dict[str, dict]:
    if not isinstance(values, list):
        raise ValueError("Invalid weapon catalog")
    result = {}
    for value in values:
        if not isinstance(value, dict):
            raise ValueError("Invalid weapon definition")
        identity = configuration_id(value["Id"])
        WeaponProgression.parse(value)
        if identity in result:
            raise ValueError("Duplicate weapon definition")
        result[identity] = value
    return result
