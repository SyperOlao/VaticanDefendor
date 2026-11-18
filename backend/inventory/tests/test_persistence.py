from uuid import uuid4
import pytest
from sqlalchemy import text
from sqlalchemy.exc import IntegrityError
from vatican_inventory.migrate import migrate, SCHEMA_REVISION
from vatican_inventory.persistence.postgres.tables import OwnedWeapon, WeaponLoadout


def test_weapon_loadout_cannot_reference_another_owners_instance(inventory_storage):
    database, _ = inventory_storage
    owner, other = uuid4(), uuid4()
    instance = uuid4()
    with database.transaction() as session:
        session.add(OwnedWeapon(instance_id=instance, account_id=owner, configuration_id="audit.weapon", grant_key="audit"))
    with pytest.raises(IntegrityError):
        with database.transaction() as session:
            session.add(WeaponLoadout(account_id=other, slot=0, instance_id=instance))
    # Definitions belong to game_backend; the reference is intentionally not a database FK.
    with database.transaction() as session:
        session.add(OwnedWeapon(instance_id=uuid4(), account_id=owner, configuration_id="remote.config", grant_key="remote"))


def test_repeated_migration_preserves_inventory_state(inventory):
    inventory.api.receive(uuid4(), 'level.reward', 1, {'accountId': str(inventory.account),
        'claimId': str(uuid4()), 'itemIndex': 0, 'rewardType': 'ITEM', 'rewardRef': 'kit', 'quantity': 2})
    before = inventory.api.get(inventory.account)
    migrate(inventory.url)
    migrate(inventory.url)
    assert inventory.api.get(inventory.account) == before
    assert inventory.api.item(inventory.account, 'item:kit')['quantity'] == 2
    with inventory.database.engine.connect() as connection:
        assert connection.scalar(text('SELECT version_num FROM alembic_version')) == SCHEMA_REVISION


