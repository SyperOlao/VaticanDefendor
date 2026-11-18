from alembic import command
from alembic.script import ScriptDirectory
from sqlalchemy import text

from vatican_shop.migrate import SCHEMA_REVISION, migrate, migration_config


def test_single_baseline_recreates_empty_catalog_and_repeated_startup_preserves_state(service_storage_factory):
    database, url = service_storage_factory('shop')
    config = migration_config()
    assert [revision.revision for revision in ScriptDirectory.from_config(config).walk_revisions()] == [SCHEMA_REVISION]
    for _ in range(2):
        with database.engine.begin() as connection:
            assert connection.execute(text('SELECT id, revision FROM shop_catalog_state')).one() == (1, 1)
            assert connection.scalar(text('SELECT count(*) FROM shop_offers')) == 0
            assert connection.scalar(text('SELECT count(*) FROM mock_order_states')) == 0
            connection.execute(text('UPDATE shop_catalog_state SET revision=3'))
        migrate(url)
        with database.engine.begin() as connection:
            assert connection.scalar(text('SELECT revision FROM shop_catalog_state')) == 3
            config.attributes['connection'] = connection
            command.downgrade(config, 'base')
        config.attributes.pop('connection')
        migrate(url)
