from copy import deepcopy
from uuid import uuid4

import pytest

from vatican_inventory.domain.run_rewards import RunReward
from vatican_inventory.domain.weapons import MAX_INTEGER, WeaponProgression


def payload():
    weapon = str(uuid4())
    return {"accountId": str(uuid4()), "runId": str(uuid4()), "configurationId": "axe.rare", "snapshot": {
        "Configurations": [{"Id": "sword.common", "ExperiencePerLevel": 100, "MaximumLevel": 20}],
        "Weapons": [{"InstanceId": weapon, "ConfigurationId": "sword.common", "Level": 1, "Experience": 0}],
        "Loadout": [weapon], "Revision": 1,
    }}


@pytest.mark.parametrize("change", [
    lambda p: p.update(snapshot=None),
    lambda p: p.update(snapshot=[]),
    lambda p: p.update(configurationId=[]),
    lambda p: p["snapshot"].update(Weapons=[]),
    lambda p: p["snapshot"].update(Loadout=[]),
    lambda p: p["snapshot"].update(Revision=True),
    lambda p: p["snapshot"]["Weapons"].append(deepcopy(p["snapshot"]["Weapons"][0])),
    lambda p: p["snapshot"]["Weapons"][0].update(Level=True),
    lambda p: p["snapshot"]["Weapons"][0].update(Experience=-1),
    lambda p: p["snapshot"]["Weapons"][0].update(ConfigurationId="missing"),
    lambda p: p["snapshot"]["Weapons"][0].update(InstanceId="invalid"),
    lambda p: p["snapshot"]["Configurations"][0].update(ExperiencePerLevel=0),
    lambda p: p["snapshot"]["Configurations"][0].update(MaximumLevel=True),
    lambda p: p["snapshot"]["Configurations"].append(deepcopy(p["snapshot"]["Configurations"][0])),
    lambda p: p["snapshot"].update(Loadout=[str(uuid4())]),
    lambda p: p.update(extra="unexpected"),
])
def test_run_contract_rejects_malformed_or_ambiguous_snapshots(change):
    value = payload()
    change(value)
    with pytest.raises((ValueError, KeyError, TypeError, AttributeError)):
        RunReward.parse("run.weapon_reward", 1, value)


@pytest.mark.parametrize("version", [True, "1", 1.0, 0, 2])
def test_version_is_an_exact_supported_integer(version):
    with pytest.raises(ValueError):
        RunReward.parse("run.weapon_reward", version, payload())


def test_progression_respects_max_level_preserves_existing_level_and_bounds_storage():
    rules = WeaponProgression(100, 3)
    assert rules.advance(1, 0) == (2, 100)
    assert rules.advance(3, 1000) == (3, 1100)
    assert rules.advance(5, 1000) == (5, 1100)
    with pytest.raises(ValueError):
        rules.advance(3, MAX_INTEGER)
