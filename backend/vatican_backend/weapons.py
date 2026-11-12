import json
import math
import secrets
from pathlib import Path
from uuid import UUID, uuid4, uuid5

from fastapi import Depends, HTTPException
from fastapi.responses import Response
from pydantic import BaseModel, ConfigDict, Field
from psycopg.types.json import Jsonb


class WeaponLoadoutRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    InstanceIds: list[UUID] = Field(min_length=1, max_length=3)


def LoadCatalog():
    Catalog = json.loads((Path(__file__).parent.parent / "weapon-catalog.json").read_text(encoding="utf-8"))
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
        if not (Path(__file__).parent.parent / "media" / (Model + ".png")).is_file():
            raise ValueError("Weapon media is missing")
        Identifiers.add(Identifier)
    if "sword.common" not in Identifiers:
        raise ValueError("Starter sword is missing")
    return Catalog


def RegisterCatalog(Database, Catalog):
    """Persist definitions once; balance updates require a new configuration ID."""
    for Configuration in sorted(Catalog, key=lambda Entry: Entry["Id"]):
        Database.execute(
            "INSERT INTO weapon_configurations(configuration_id,configuration_json) VALUES (%s,%s) "
            "ON CONFLICT(configuration_id) DO NOTHING", (Configuration["Id"], Jsonb(Configuration)))
        Stored = Database.execute(
            "SELECT configuration_json FROM weapon_configurations WHERE configuration_id=%s",
            (Configuration["Id"],)).fetchone()
        if Stored["configuration_json"] != Configuration:
            raise ValueError("Weapon configuration is immutable; use a new Id: " + Configuration["Id"])
    Missing = Database.execute(
        "SELECT configuration_id FROM owned_weapons WHERE configuration_id NOT IN "
        "(SELECT configuration_id FROM weapon_configurations) LIMIT 1").fetchone()
    if Missing:
        raise ValueError("Restore the definition of owned weapon: " + Missing["configuration_id"])


def EnsureInventory(Database, AccountId):
    Database.execute("SELECT id FROM accounts WHERE id=%s FOR UPDATE", (AccountId,))
    StarterId = uuid5(AccountId, "weapon.starter.sword")
    Starter = Database.execute(
        "INSERT INTO owned_weapons(instance_id,account_id,configuration_id,grant_key) "
        "VALUES (%s,%s,'sword.common','starter') ON CONFLICT(account_id,grant_key) DO NOTHING RETURNING instance_id",
        (StarterId, AccountId)).fetchone()
    Selection = Database.execute(
        "INSERT INTO weapon_loadouts(account_id,slot,instance_id) "
        "SELECT %s,0,%s WHERE NOT EXISTS (SELECT 1 FROM weapon_loadouts WHERE account_id=%s) "
        "ON CONFLICT DO NOTHING RETURNING instance_id", (AccountId, StarterId, AccountId)).fetchone()
    if Starter or Selection:
        Database.execute("UPDATE profiles SET inventory_revision=inventory_revision+1 WHERE account_id=%s", (AccountId,))


def ReadInventory(Database, AccountId, Catalog):
    EnsureInventory(Database, AccountId)
    Weapons = Database.execute(
        "SELECT instance_id,configuration_id,level,experience FROM owned_weapons "
        "WHERE account_id=%s ORDER BY created_at,instance_id", (AccountId,)).fetchall()
    Loadout = Database.execute(
        "SELECT instance_id FROM weapon_loadouts WHERE account_id=%s ORDER BY slot", (AccountId,)).fetchall()
    Revision = Database.execute("SELECT inventory_revision FROM profiles WHERE account_id=%s", (AccountId,)).fetchone()
    Definitions = {Entry["Id"]: Entry for Entry in Catalog}
    for Entry in Database.execute(
            "SELECT configuration_json FROM weapon_configurations WHERE configuration_id=ANY(%s)",
            (list({Weapon["configuration_id"] for Weapon in Weapons}),)):
        Definition = Entry["configuration_json"]
        Definitions[Definition["Id"]] = Definition
    return {"Configurations": list(Definitions.values()),
            "Weapons": [{"InstanceId": str(Weapon["instance_id"]),
                         "ConfigurationId": Weapon["configuration_id"],
                         "Level": Weapon["level"], "Experience": Weapon["experience"]} for Weapon in Weapons],
            "Loadout": [str(Entry["instance_id"]) for Entry in Loadout],
            "Revision": Revision["inventory_revision"]}


def PinLoadout(Database, AccountId, RunId, Catalog):
    Inventory = ReadInventory(Database, AccountId, Catalog)
    Selected = set(Inventory["Loadout"])
    Inventory["Weapons"] = [Weapon for Weapon in Inventory["Weapons"] if Weapon["InstanceId"] in Selected]
    Database.execute("UPDATE runs SET weapon_snapshot=%s WHERE id=%s", (Jsonb(Inventory), RunId))
    return Inventory


def GrantMatchWeapon(Database, AccountId, RunId, Catalog):
    Database.execute("SELECT id FROM accounts WHERE id=%s FOR UPDATE", (AccountId,))
    GrantKey = "run:" + str(RunId)
    Existing = Database.execute(
        "SELECT instance_id FROM owned_weapons WHERE account_id=%s AND grant_key=%s",
        (AccountId, GrantKey)).fetchone()
    if Existing:
        return str(Existing["instance_id"])
    Configuration = Catalog[secrets.randbelow(len(Catalog))]
    InstanceId = uuid4()
    Database.execute(
        "INSERT INTO owned_weapons(instance_id,account_id,configuration_id,grant_key) VALUES (%s,%s,%s,%s)",
        (InstanceId, AccountId, Configuration["Id"], GrantKey))
    Snapshot = Database.execute("SELECT weapon_snapshot FROM runs WHERE id=%s", (RunId,)).fetchone()
    if Snapshot and Snapshot["weapon_snapshot"]:
        Configurations = {Entry["Id"]: Entry for Entry in Snapshot["weapon_snapshot"]["Configurations"]}
        for Weapon in Snapshot["weapon_snapshot"]["Weapons"]:
            Definition = Configurations[Weapon["ConfigurationId"]]
            Experience = Weapon["Experience"] + Definition["ExperiencePerLevel"]
            Level = min(Definition["MaximumLevel"], 1 + Experience // Definition["ExperiencePerLevel"])
            Database.execute("UPDATE owned_weapons SET experience=%s,level=%s WHERE account_id=%s AND instance_id=%s",
                             (Experience, Level, AccountId, UUID(Weapon["InstanceId"])))
    Database.execute("UPDATE profiles SET inventory_revision=inventory_revision+1 WHERE account_id=%s", (AccountId,))
    return str(InstanceId)


def InstallWeapons(Application, Connect, AccountFromBearer, Catalog):
    @Application.get("/v1/weapons")
    def GetWeapons(AccountId: UUID = Depends(AccountFromBearer)):
        with Connect() as Database:
            with Database.transaction():
                return ReadInventory(Database, AccountId, Catalog)

    @Application.put("/v1/weapons/loadout")
    def SetLoadout(Body: WeaponLoadoutRequest, AccountId: UUID = Depends(AccountFromBearer)):
        if len(set(Body.InstanceIds)) != len(Body.InstanceIds):
            raise HTTPException(422, "Duplicate weapon selection")
        with Connect() as Database:
            with Database.transaction():
                EnsureInventory(Database, AccountId)
                if Database.execute("SELECT 1 FROM runs WHERE account_id=%s AND status IN ('pending','ready')",
                                    (AccountId,)).fetchone():
                    Current = ReadInventory(Database, AccountId, Catalog)
                    if Current["Loadout"] == [str(Identifier) for Identifier in Body.InstanceIds]:
                        return Current
                    raise HTTPException(409, "Loadout is pinned for the active run")
                Owned = Database.execute(
                    "SELECT instance_id FROM owned_weapons WHERE account_id=%s AND instance_id=ANY(%s)",
                    (AccountId, Body.InstanceIds)).fetchall()
                if len(Owned) != len(Body.InstanceIds):
                    raise HTTPException(403, "Weapon is not owned")
                Database.execute("DELETE FROM weapon_loadouts WHERE account_id=%s", (AccountId,))
                for Slot, InstanceId in enumerate(Body.InstanceIds):
                    Database.execute("INSERT INTO weapon_loadouts(account_id,slot,instance_id) VALUES (%s,%s,%s)",
                                     (AccountId, Slot, InstanceId))
                Database.execute("UPDATE profiles SET version=version+1,inventory_revision=inventory_revision+1 WHERE account_id=%s", (AccountId,))
                return ReadInventory(Database, AccountId, Catalog)

    @Application.get("/v1/weapons/media/{ConfigurationId}.png")
    def GetWeaponImage(ConfigurationId: str):
        Configuration = next((Entry for Entry in Catalog if Entry["Id"] == ConfigurationId), None)
        if Configuration is None:
            with Connect() as Database:
                Stored = Database.execute(
                    "SELECT configuration_json FROM weapon_configurations WHERE configuration_id=%s",
                    (ConfigurationId,)).fetchone()
            if Stored is None:
                raise HTTPException(404, "Weapon image not found")
            Configuration = Stored["configuration_json"]
        Model = ["sword", "spear", "axe"][Configuration["ModelIndex"]]
        Media = Path(__file__).parent.parent / "media" / (Model + ".png")
        return Response(Media.read_bytes(), media_type="image/png", headers={"Cache-Control": "public, max-age=3600"})


