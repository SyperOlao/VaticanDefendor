import ast
import importlib
from pathlib import Path
import pytest
from alembic.autogenerate import compare_metadata
from alembic.migration import MigrationContext


SERVICES=('game_backend','wallet','inventory','shop','rating','analytics')


@pytest.mark.parametrize('service',SERVICES)
def test_migrations_match_metadata_and_all_foreign_keys_are_local(service,service_storage_factory):
    db,url=service_storage_factory(service)
    package='vatican_backend' if service=='game_backend' else 'vatican_'+service
    persistence=importlib.import_module(package+'.persistence.postgres')
    importlib.import_module(package+'.migrate').migrate(url)
    migration=importlib.import_module(package+'.migrate')
    from alembic.script import ScriptDirectory
    revisions=list(ScriptDirectory.from_config(migration.migration_config()).walk_revisions())
    assert len(revisions)==1 and revisions[0].revision==migration.SCHEMA_REVISION
    metadata=persistence.Base.metadata
    with db.engine.connect() as connection:
        assert compare_metadata(MigrationContext.configure(connection,opts={'compare_type':True,'compare_server_default':True}),metadata)==[]
    assert all(fk.column.table.metadata is metadata for table in metadata.tables.values() for fk in table.foreign_keys)
    if service=='game_backend':
        assert not {'currencies','currency_balances','economy_transactions','owned_weapons','inventory',
                    'weapon_loadouts','purchase_operations','payment_orders','leaderboards','leaderboard_scores'} & metadata.tables.keys()
        assert {'energy_configs','player_effects','reward_bundles','case_definitions','weapon_configurations'} <= metadata.tables.keys()
        assert not {'permanent_currency','wallet_revision','inventory_revision'} & set(metadata.tables['profiles'].columns.keys())


@pytest.mark.parametrize('service',SERVICES)
def test_business_layer_has_no_framework_or_orm_imports(service):
    package='vatican_backend' if service=='game_backend' else 'vatican_'+service
    folder=Path(__file__).resolve().parents[1]/service/package/'application'
    for path in folder.glob('*.py'):
        for node in ast.walk(ast.parse(path.read_text())):
            if isinstance(node,ast.Import):
                imports=[a.name.split('.')[0] for a in node.names]
            elif isinstance(node,ast.ImportFrom):
                imports=(node.module or '').split('.')
            else:
                continue
            assert not set(imports)&{'fastapi','starlette','pydantic','sqlalchemy','psycopg','dishka','persistence','bootstrap'},path

