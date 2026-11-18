import json
import math
from pathlib import Path
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field


def LoadCatalog():
    Catalog = json.loads((Path(__file__).resolve().parents[2] / "weapon-catalog.json").read_text(encoding="utf-8"))
    Identifiers = set()
    for Configuration in Catalog:
        Identifier = Configuration["Id"]
        NumericFields = ("Damage", "AttackInterval", "Range", "DamagePerLevel", "SpeedPerLevel", "SpecialDamageMultiplier", "SpecialRadius", "SpecialDuration", "SpecialCooldown", "HalfWidth", "TipWidthScale", "DashDamageMultiplier", "DashDistance", "DashRange", "DashDuration", "SpecialDistance", "FinishDamageMultiplier", "FinishRadius", "SpecialPush", "LaunchHeight", "JumpHeight", "DefenseRadius", "DefensePush", "DefenseDuration", "DefenseShield", "DefenseHealing", "DefenseStamina", "DefenseDamageBonus", "DefenseDamageDuration", "DefensePulse")
        if any(not isinstance(Configuration[Field], (int, float)) or isinstance(Configuration[Field], bool) or
               not math.isfinite(Configuration[Field]) or Configuration[Field] < 0 for Field in NumericFields):
            raise ValueError("Invalid weapon parameter")
        if Configuration["ModelIndex"] not in range(3) or min(Configuration["Range"], Configuration["DashRange"], Configuration["DashDuration"]) <= 0:
            raise ValueError("Invalid weapon model or range")
        if Identifier in Identifiers or Configuration["Damage"] <= 0 or Configuration["AttackInterval"] <= 0:
            raise ValueError("Invalid weapon catalog")
        if Configuration["DefenseAbility"] not in range(6) or Configuration["SpecialAbility"] not in range(6) or Configuration["Rarity"] not in range(4):
            raise ValueError("Unsupported weapon behavior")
        if Configuration["MaximumLevel"] < 1 or Configuration["ExperiencePerLevel"] < 1:
            raise ValueError("Invalid weapon progression")
        Model = ["sword", "spear", "axe"][Configuration["ModelIndex"]]
        if not (Path(__file__).resolve().parents[2] / "media" / (Model + ".png")).is_file():
            raise ValueError("Weapon media is missing")
        Identifiers.add(Identifier)
    if "sword.common" not in Identifiers:
        raise ValueError("Starter sword is missing")
    return Catalog

