import secrets
from uuid import UUID, uuid4, uuid5

from .errors import ApplicationError


class WeaponUseCases:
    def __init__(self, uow, catalog):
        self.uow = uow
        self.catalog = catalog

    def register_catalog(self):
        with self.uow() as work:
            for definition in sorted(self.catalog, key=lambda entry: entry["Id"]):
                stored = work.weapons.register_configuration(definition)
                if stored.configuration_json != definition:
                    raise ValueError("Weapon configuration is immutable; use a new Id: " + definition["Id"])
            missing = work.weapons.missing_configuration()
            if missing:
                raise ValueError("Restore the definition of owned weapon: " + missing)

    def ensure_inventory(self, work, account_id):
        work.profiles.get(account_id, lock=True)
        starter_id = uuid5(account_id, "weapon.starter.sword")
        created = work.weapons.ensure_starter(account_id, starter_id)
        selected = False
        if not work.weapons.loadout(account_id):
            work.weapons.select_starter(account_id, starter_id)
            selected = True
        if created or selected:
            work.profiles.bump_inventory(account_id)

    def read_inventory(self, work, account_id):
        self.ensure_inventory(work, account_id)
        weapons = work.weapons.owned(account_id)
        loadout = work.weapons.loadout(account_id)
        profile = work.profiles.get(account_id)
        definitions = {entry["Id"]: entry for entry in self.catalog}
        for definition in work.weapons.definitions({weapon.configuration_id for weapon in weapons}):
            definitions[definition["Id"]] = definition
        return {"Configurations": list(definitions.values()),
                "Weapons": [{"InstanceId": str(weapon.instance_id), "ConfigurationId": weapon.configuration_id,
                             "Level": weapon.level, "Experience": weapon.experience} for weapon in weapons],
                "Loadout": [str(instance_id) for instance_id in loadout], "Revision": profile.inventory_revision}

    def get_inventory(self, account_id):
        with self.uow() as work:
            return self.read_inventory(work, account_id)

    def pin_loadout(self, work, account_id, run):
        inventory = self.read_inventory(work, account_id)
        selected = set(inventory["Loadout"])
        inventory["Weapons"] = [weapon for weapon in inventory["Weapons"] if weapon["InstanceId"] in selected]
        run.weapon_snapshot = inventory

    def grant_match_weapon(self, work, account_id, run):
        work.profiles.get(account_id, lock=True)
        key = "run:" + str(run.id)
        existing = work.weapons.grant(account_id, key)
        if existing:
            return str(existing.instance_id)
        configuration = self.catalog[secrets.randbelow(len(self.catalog))]
        instance_id = uuid4()
        work.weapons.add(instance_id=instance_id, account_id=account_id,
                          configuration_id=configuration["Id"], grant_key=key)
        if run.weapon_snapshot:
            configurations = {entry["Id"]: entry for entry in run.weapon_snapshot["Configurations"]}
            for weapon in run.weapon_snapshot["Weapons"]:
                definition = configurations[weapon["ConfigurationId"]]
                experience = weapon["Experience"] + definition["ExperiencePerLevel"]
                level = min(definition["MaximumLevel"], 1 + experience // definition["ExperiencePerLevel"])
                work.weapons.progress(account_id, UUID(weapon["InstanceId"]), experience, level)
        work.profiles.bump_inventory(account_id)
        return str(instance_id)

    def set_loadout(self, account_id, instance_ids):
        if len(set(instance_ids)) != len(instance_ids):
            raise ApplicationError(422, "Duplicate weapon selection")
        with self.uow() as work:
            self.ensure_inventory(work, account_id)
            if work.runs.active(account_id):
                current = self.read_inventory(work, account_id)
                if current["Loadout"] == [str(instance_id) for instance_id in instance_ids]:
                    return current
                raise ApplicationError(409, "Loadout is pinned for the active run")
            owned = {weapon.instance_id for weapon in work.weapons.owned(account_id)}
            if not set(instance_ids) <= owned:
                raise ApplicationError(403, "Weapon is not owned")
            work.weapons.replace_loadout(account_id, instance_ids)
            work.profiles.bump_inventory(account_id, bump_version=True)
            return self.read_inventory(work, account_id)

    def image_configuration(self, configuration_id):
        configuration = next((entry for entry in self.catalog if entry["Id"] == configuration_id), None)
        if configuration is None:
            with self.uow() as work:
                stored = work.weapons.configuration(configuration_id)
                if stored is None:
                    raise ApplicationError(404, "Weapon image not found")
                configuration = stored.configuration_json
        return configuration
