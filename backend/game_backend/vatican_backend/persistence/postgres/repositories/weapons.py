from sqlalchemy import delete, exists, select, update
from sqlalchemy.dialects.postgresql import insert

from ..tables import InventoryItem, OwnedWeapon, WeaponConfiguration, WeaponLoadout


class WeaponsRepository:
    def __init__(self, session):
        self.session = session

    def register_configuration(self, configuration):
        self.session.execute(insert(WeaponConfiguration).values(
            configuration_id=configuration["Id"], configuration_json=configuration
        ).on_conflict_do_nothing(index_elements=[WeaponConfiguration.configuration_id]))
        return self.configuration(configuration["Id"])

    def configuration(self, configuration_id):
        return self.session.get(WeaponConfiguration, configuration_id)

    def missing_configuration(self):
        return self.session.scalar(select(OwnedWeapon.configuration_id).where(
            ~exists().where(WeaponConfiguration.configuration_id == OwnedWeapon.configuration_id)).limit(1))

    def ensure_starter(self, account_id, instance_id):
        return self.session.scalar(insert(OwnedWeapon).values(
            instance_id=instance_id, account_id=account_id, configuration_id="sword.common", grant_key="starter"
        ).on_conflict_do_nothing(index_elements=[OwnedWeapon.account_id, OwnedWeapon.grant_key])
            .returning(OwnedWeapon.instance_id))

    def loadout(self, account_id):
        return list(self.session.scalars(select(WeaponLoadout.instance_id).where(
            WeaponLoadout.account_id == account_id).order_by(WeaponLoadout.slot)))

    def owned(self, account_id):
        return list(self.session.scalars(select(OwnedWeapon).where(OwnedWeapon.account_id == account_id)
                                        .order_by(OwnedWeapon.created_at, OwnedWeapon.instance_id)))

    def definitions(self, ids):
        return list(self.session.scalars(select(WeaponConfiguration.configuration_json).where(
            WeaponConfiguration.configuration_id.in_(ids))))

    def add(self, **values):
        self.session.add(OwnedWeapon(**values))

    def grant(self, account_id, key):
        return self.session.scalar(select(OwnedWeapon).where(
            OwnedWeapon.account_id == account_id, OwnedWeapon.grant_key == key))

    def replace_loadout(self, account_id, instance_ids):
        self.session.execute(delete(WeaponLoadout).where(WeaponLoadout.account_id == account_id))
        for slot, instance_id in enumerate(instance_ids):
            self.session.add(WeaponLoadout(account_id=account_id, slot=slot, instance_id=instance_id))

    def select_starter(self, account_id, instance_id):
        self.session.add(WeaponLoadout(account_id=account_id, slot=0, instance_id=instance_id))

    def progress(self, account_id, instance_id, experience, level):
        self.session.execute(update(OwnedWeapon).where(
            OwnedWeapon.account_id == account_id, OwnedWeapon.instance_id == instance_id
        ).values(experience=experience, level=level))

    def legacy_inventory(self, account_id):
        return list(self.session.scalars(select(InventoryItem).where(
            InventoryItem.account_id == account_id).order_by(InventoryItem.item_id)))
