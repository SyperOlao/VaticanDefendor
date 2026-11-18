"""Version 1 of the game service's pinned inventory reward contract."""
from dataclasses import dataclass
import hashlib
import json
from uuid import UUID

from .weapons import MAX_INTEGER, MAX_LOADOUT_SIZE, WeaponProgression, configuration_id, definition_map


@dataclass(frozen=True)
class PinnedWeapon:
    instance_id: UUID
    configuration_id: str
    progression: WeaponProgression


@dataclass(frozen=True)
class RunReward:
    account_id: UUID
    run_id: UUID
    configuration_id: str
    weapons: tuple[PinnedWeapon, ...]
    digest: bytes

    @classmethod
    def parse(cls, event_name, schema_version, payload):
        if (event_name != "run.weapon_reward" or type(schema_version) is not int or schema_version != 1
                or not isinstance(payload, dict)
                or set(payload) != {"accountId", "runId", "configurationId", "snapshot"}):
            raise ValueError("Invalid run reward")
        account, run = UUID(payload["accountId"]), UUID(payload["runId"])
        reward_configuration = configuration_id(payload["configurationId"])
        snapshot = payload["snapshot"]
        if not isinstance(snapshot, dict) or set(snapshot) != {"Configurations", "Weapons", "Loadout", "Revision"}:
            raise ValueError("Invalid inventory snapshot")
        definitions = definition_map(snapshot["Configurations"])
        weapons, loadout = snapshot["Weapons"], snapshot["Loadout"]
        if (not isinstance(weapons, list) or not 1 <= len(weapons) <= MAX_LOADOUT_SIZE
                or not isinstance(loadout, list) or len(loadout) != len(weapons)
                or type(snapshot["Revision"]) is not int or snapshot["Revision"] < 1):
            raise ValueError("Invalid pinned loadout")
        pinned = []
        for weapon in weapons:
            if not isinstance(weapon, dict) or set(weapon) != {"InstanceId", "ConfigurationId", "Level", "Experience"}:
                raise ValueError("Invalid pinned weapon")
            identity, config = UUID(weapon["InstanceId"]), configuration_id(weapon["ConfigurationId"])
            if (type(weapon["Level"]) is not int or not 1 <= weapon["Level"] <= MAX_INTEGER
                    or type(weapon["Experience"]) is not int or not 0 <= weapon["Experience"] <= MAX_INTEGER):
                raise ValueError("Invalid pinned progress")
            pinned.append(PinnedWeapon(identity, config, WeaponProgression.parse(definitions[config])))
        identities = {weapon.instance_id for weapon in pinned}
        selected = [UUID(value) for value in loadout]
        if len(identities) != len(pinned) or len(set(selected)) != len(selected) or set(selected) != identities:
            raise ValueError("Pinned weapons do not match loadout")
        # Preserve the original v1 hash so existing consumed_messages remain comparable.
        digest = hashlib.sha256(json.dumps([event_name, schema_version, payload], sort_keys=True, allow_nan=False).encode()).digest()
        return cls(account, run, reward_configuration, tuple(pinned), digest)
