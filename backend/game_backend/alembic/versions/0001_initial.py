"""Initial game-owned PostgreSQL schema, excluding auth service tables."""
from alembic import op
import sqlalchemy as sa
from sqlalchemy.dialects import postgresql

revision = "0001_initial"
down_revision = None
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.create_table('account_preferences',
    sa.Column('account_id', sa.Uuid(), nullable=False),
    sa.Column('display_timezone_iana', sa.String(length=64), server_default='UTC', nullable=False),
    sa.Column('locale', sa.String(length=16), server_default='ru', nullable=False),
    sa.Column('avatar_asset_key', sa.String(length=128), nullable=True),
    sa.PrimaryKeyConstraint('account_id', name=op.f('account_preferences_pkey'))
    )
    op.create_table('case_types',
    sa.Column('code', sa.String(length=64), nullable=False),
    sa.PrimaryKeyConstraint('code', name=op.f('case_types_pkey'))
    )
    op.create_table('consumed_messages',
    sa.Column('consumer', sa.String(length=64), nullable=False),
    sa.Column('message_id', sa.Uuid(), nullable=False),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.PrimaryKeyConstraint('consumer', 'message_id', name=op.f('consumed_messages_pkey'))
    )
    op.create_table('content_releases',
    sa.Column('id', sa.String(length=128), nullable=False),
    sa.Column('schema_version', sa.Integer(), nullable=False),
    sa.Column('payload_json', sa.Text(), nullable=False),
    sa.Column('payload_sha256', sa.String(length=64), nullable=False),
    sa.Column('published_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.PrimaryKeyConstraint('id', name=op.f('content_releases_pkey'))
    )
    op.create_table('currencies',
    sa.Column('code', sa.String(length=32), nullable=False),
    sa.PrimaryKeyConstraint('code', name=op.f('currencies_pkey'))
    )
    op.create_table('game_servers',
    sa.Column('host', sa.String(length=255), nullable=False),
    sa.Column('port', sa.Integer(), nullable=False),
    sa.Column('content_release_id', sa.String(length=128), nullable=False),
    sa.Column('protocol_version', sa.Integer(), nullable=False),
    sa.Column('generation', sa.Integer(), server_default='0', nullable=False),
    sa.Column('current_run_id', sa.Uuid(), nullable=True),
    sa.Column('status', sa.String(length=16), nullable=False),
    sa.Column('last_seen', sa.DateTime(timezone=True), nullable=False),
    sa.Column('id', sa.Uuid(), nullable=False),
    sa.CheckConstraint("status IN ('ready', 'assigned', 'occupied', 'unavailable')", name=op.f('ck_game_servers_status')),
    sa.CheckConstraint('port BETWEEN 1 AND 65535', name=op.f('ck_game_servers_port_range')),
    sa.PrimaryKeyConstraint('id', name=op.f('game_servers_pkey'))
    )
    op.create_table('idempotency_records',
    sa.Column('account_id', sa.Uuid(), nullable=False),
    sa.Column('operation', sa.String(length=64), nullable=False),
    sa.Column('request_key', sa.String(length=128), nullable=False),
    sa.Column('payload_hash', sa.LargeBinary(), nullable=False),
    sa.Column('status', sa.String(length=16), server_default='PENDING', nullable=False),
    sa.Column('result_reference', sa.String(length=255), nullable=True),
    sa.Column('result_json', postgresql.JSONB(astext_type=sa.Text()), nullable=True),
    sa.Column('completed_at', sa.DateTime(timezone=True), nullable=True),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.CheckConstraint("(status IN ('COMPLETED','REJECTED')) = (completed_at IS NOT NULL)", name=op.f('ck_idempotency_records_completion_time')),
    sa.CheckConstraint("status IN ('PENDING','UNKNOWN','COMPLETED','REJECTED')", name=op.f('ck_idempotency_records_status')),
    sa.CheckConstraint('octet_length(payload_hash) = 32', name=op.f('ck_idempotency_records_hash_length')),
    sa.PrimaryKeyConstraint('account_id', 'operation', 'request_key', name=op.f('idempotency_records_pkey'))
    )
    op.create_table('inventory',
    sa.Column('account_id', sa.Uuid(), nullable=False),
    sa.Column('item_id', sa.String(length=64), nullable=False),
    sa.Column('quantity', sa.Integer(), nullable=False),
    sa.CheckConstraint('quantity >= 0', name=op.f('ck_inventory_quantity_nonnegative')),
    sa.PrimaryKeyConstraint('account_id', 'item_id', name=op.f('inventory_pkey'))
    )
    op.create_table('leaderboards',
    sa.Column('code', sa.String(length=64), nullable=False),
    sa.Column('score_source', sa.String(length=64), nullable=False),
    sa.Column('id', sa.Uuid(), nullable=False),
    sa.PrimaryKeyConstraint('id', name=op.f('leaderboards_pkey')),
    sa.UniqueConstraint('code', name=op.f('leaderboards_code_key'))
    )
    op.create_table('payment_methods',
    sa.Column('id', sa.Integer(), nullable=False),
    sa.Column('code', sa.String(length=32), nullable=False),
    sa.Column('kind', sa.String(length=32), nullable=False),
    sa.PrimaryKeyConstraint('id', name=op.f('payment_methods_pkey')),
    sa.UniqueConstraint('code', name=op.f('payment_methods_code_key'))
    )
    op.create_table('payment_providers',
    sa.Column('id', sa.Integer(), nullable=False),
    sa.Column('code', sa.String(length=32), nullable=False),
    sa.Column('provider_kind', sa.String(length=32), nullable=False),
    sa.Column('enabled', sa.Boolean(), server_default='false', nullable=False),
    sa.PrimaryKeyConstraint('id', name=op.f('payment_providers_pkey')),
    sa.UniqueConstraint('code', name=op.f('payment_providers_code_key'))
    )
    op.create_table('player_effects',
    sa.Column('account_id', sa.Uuid(), nullable=False),
    sa.Column('effect_type', sa.String(length=64), nullable=False),
    sa.Column('modifier_value', sa.Numeric(precision=18, scale=6), nullable=False),
    sa.Column('source_key', sa.String(length=128), nullable=False),
    sa.Column('starts_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.Column('expires_at', sa.DateTime(timezone=True), nullable=False),
    sa.Column('id', sa.Uuid(), nullable=False),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.CheckConstraint('expires_at > starts_at', name=op.f('ck_player_effects_effect_window')),
    sa.PrimaryKeyConstraint('id', name=op.f('player_effects_pkey')),
    sa.UniqueConstraint('account_id', 'source_key', name=op.f('player_effects_account_id_source_key_key'))
    )
    op.create_index(op.f('ix_player_effects_account_id'), 'player_effects', ['account_id'], unique=False)
    op.create_table('player_level_progress',
    sa.Column('account_id', sa.Uuid(), nullable=False),
    sa.Column('total_xp', sa.BigInteger(), server_default='0', nullable=False),
    sa.Column('current_level', sa.Integer(), server_default='1', nullable=False),
    sa.CheckConstraint('current_level > 0', name=op.f('ck_player_level_progress_level_positive')),
    sa.CheckConstraint('total_xp >= 0', name=op.f('ck_player_level_progress_xp_nonnegative')),
    sa.PrimaryKeyConstraint('account_id', name=op.f('player_level_progress_pkey'))
    )
    op.create_table('prestige_upgrade_types',
    sa.Column('code', sa.String(length=64), nullable=False),
    sa.PrimaryKeyConstraint('code', name=op.f('prestige_upgrade_types_pkey'))
    )
    op.create_table('profiles',
    sa.Column('account_id', sa.Uuid(), nullable=False),
    sa.Column('version', sa.Integer(), nullable=False),
    sa.Column('display_name', sa.String(length=32), nullable=False),
    sa.Column('permanent_currency', sa.BigInteger(), server_default='500', nullable=False),
    sa.Column('wallet_revision', sa.BigInteger(), server_default='1', nullable=False),
    sa.Column('inventory_revision', sa.BigInteger(), server_default='1', nullable=False),
    sa.Column('updated_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.CheckConstraint('inventory_revision > 0', name=op.f('ck_profiles_inventory_revision_positive')),
    sa.CheckConstraint('permanent_currency >= 0', name=op.f('ck_profiles_currency_nonnegative')),
    sa.CheckConstraint('version > 0', name=op.f('ck_profiles_version_positive')),
    sa.CheckConstraint('wallet_revision > 0', name=op.f('ck_profiles_wallet_revision_positive')),
    sa.PrimaryKeyConstraint('account_id', name=op.f('profiles_pkey'))
    )
    op.create_table('purchase_operations',
    sa.Column('account_id', sa.Uuid(), nullable=False),
    sa.Column('operation_id', sa.Uuid(), nullable=False),
    sa.Column('offer_id', sa.String(length=64), nullable=False),
    sa.Column('catalog_version', sa.String(length=64), nullable=False),
    sa.Column('receipt_json', postgresql.JSONB(astext_type=sa.Text()), nullable=False),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.PrimaryKeyConstraint('account_id', 'operation_id', name=op.f('purchase_operations_pkey'))
    )
    op.create_index('purchase_operations_completed_offer_idx', 'purchase_operations', ['account_id', 'offer_id'], unique=False, postgresql_where=sa.text("receipt_json->>'status'='Completed'"))
    op.create_table('reward_bundles',
    sa.Column('code', sa.String(length=64), nullable=False),
    sa.Column('version', sa.Integer(), nullable=False),
    sa.Column('id', sa.Uuid(), nullable=False),
    sa.CheckConstraint('version > 0', name=op.f('ck_reward_bundles_version_positive')),
    sa.PrimaryKeyConstraint('id', name=op.f('reward_bundles_pkey')),
    sa.UniqueConstraint('code', 'version', name=op.f('reward_bundles_code_version_key'))
    )
    op.create_table('storefronts',
    sa.Column('id', sa.Integer(), nullable=False),
    sa.Column('code', sa.String(length=32), nullable=False),
    sa.Column('platform', sa.String(length=32), nullable=False),
    sa.Column('enabled', sa.Boolean(), server_default='false', nullable=False),
    sa.PrimaryKeyConstraint('id', name=op.f('storefronts_pkey')),
    sa.UniqueConstraint('code', name=op.f('storefronts_code_key'))
    )
    op.create_table('weapon_configurations',
    sa.Column('configuration_id', sa.String(length=80), nullable=False),
    sa.Column('configuration_json', postgresql.JSONB(astext_type=sa.Text()), nullable=False),
    sa.PrimaryKeyConstraint('configuration_id', name=op.f('weapon_configurations_pkey'))
    )
    op.create_table('currency_balances',
    sa.Column('account_id', sa.Uuid(), nullable=False),
    sa.Column('currency_code', sa.String(length=32), nullable=False),
    sa.Column('balance', sa.BigInteger(), server_default='0', nullable=False),
    sa.Column('revision', sa.BigInteger(), server_default='1', nullable=False),
    sa.CheckConstraint("currency_code <> 'PRESTIGE'", name=op.f('ck_currency_balances_prestige_in_profile')),
    sa.CheckConstraint('balance >= 0', name=op.f('ck_currency_balances_balance_nonnegative')),
    sa.CheckConstraint('revision > 0', name=op.f('ck_currency_balances_revision_positive')),
    sa.ForeignKeyConstraint(['currency_code'], ['currencies.code'], name=op.f('currency_balances_currency_code_fkey')),
    sa.PrimaryKeyConstraint('account_id', 'currency_code', name=op.f('currency_balances_pkey'))
    )
    op.create_table('economy_transactions',
    sa.Column('account_id', sa.Uuid(), nullable=False),
    sa.Column('currency_code', sa.String(length=32), nullable=False),
    sa.Column('delta', sa.BigInteger(), nullable=False),
    sa.Column('balance_after', sa.BigInteger(), nullable=False),
    sa.Column('reason', sa.String(length=64), nullable=False),
    sa.Column('source_type', sa.String(length=32), nullable=False),
    sa.Column('source_id', sa.Uuid(), nullable=False),
    sa.Column('line_index', sa.Integer(), server_default='0', nullable=False),
    sa.Column('id', sa.Uuid(), nullable=False),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.CheckConstraint('balance_after >= 0', name=op.f('ck_economy_transactions_balance_nonnegative')),
    sa.CheckConstraint('delta <> 0', name=op.f('ck_economy_transactions_delta_nonzero')),
    sa.CheckConstraint('line_index >= 0', name=op.f('ck_economy_transactions_line_nonnegative')),
    sa.ForeignKeyConstraint(['currency_code'], ['currencies.code'], name=op.f('economy_transactions_currency_code_fkey')),
    sa.PrimaryKeyConstraint('id', name=op.f('economy_transactions_pkey')),
    sa.UniqueConstraint('account_id', 'currency_code', 'source_type', 'source_id', 'line_index', name='uq_economy_transaction_source'),
    sa.UniqueConstraint('id', 'account_id', name=op.f('economy_transactions_id_account_id_key'))
    )
    op.create_index(op.f('ix_economy_transactions_account_id'), 'economy_transactions', ['account_id'], unique=False)
    op.create_table('leaderboard_scores',
    sa.Column('board_id', sa.Uuid(), nullable=False),
    sa.Column('account_id', sa.Uuid(), nullable=False),
    sa.Column('score', sa.BigInteger(), nullable=False),
    sa.Column('updated_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.CheckConstraint('score >= 0', name=op.f('ck_leaderboard_scores_score_nonnegative')),
    sa.ForeignKeyConstraint(['board_id'], ['leaderboards.id'], name=op.f('leaderboard_scores_board_id_fkey')),
    sa.PrimaryKeyConstraint('board_id', 'account_id', name=op.f('leaderboard_scores_pkey'))
    )
    op.create_index('ix_leaderboard_scores_ranking', 'leaderboard_scores', ['board_id', sa.literal_column('score DESC'), 'account_id'], unique=False)
    op.create_table('owned_weapons',
    sa.Column('instance_id', sa.Uuid(), nullable=False),
    sa.Column('account_id', sa.Uuid(), nullable=False),
    sa.Column('configuration_id', sa.String(length=80), nullable=False),
    sa.Column('level', sa.Integer(), server_default='1', nullable=False),
    sa.Column('experience', sa.Integer(), server_default='0', nullable=False),
    sa.Column('grant_key', sa.String(length=80), nullable=False),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.CheckConstraint('experience >= 0', name=op.f('ck_owned_weapons_experience_nonnegative')),
    sa.CheckConstraint('level >= 1', name=op.f('ck_owned_weapons_level_positive')),
    sa.ForeignKeyConstraint(['configuration_id'], ['weapon_configurations.configuration_id'], name=op.f('owned_weapons_configuration_id_fkey')),
    sa.PrimaryKeyConstraint('instance_id', name=op.f('owned_weapons_pkey')),
    sa.UniqueConstraint('account_id', 'grant_key', name='owned_weapons_account_id_grant_key_key'),
    sa.UniqueConstraint('instance_id', 'account_id', name=op.f('owned_weapons_instance_id_account_id_key'))
    )
    op.create_index('owned_weapons_account_idx', 'owned_weapons', ['account_id'], unique=False)
    op.create_table('payment_routing_rules',
    sa.Column('storefront_id', sa.Integer(), nullable=False),
    sa.Column('provider_id', sa.Integer(), nullable=False),
    sa.Column('payment_method_id', sa.Integer(), nullable=True),
    sa.Column('country_iso', sa.String(length=2), nullable=False),
    sa.Column('app_package', sa.String(length=255), nullable=False),
    sa.Column('priority', sa.Integer(), nullable=False),
    sa.Column('enabled', sa.Boolean(), server_default='false', nullable=False),
    sa.Column('id', sa.Uuid(), nullable=False),
    sa.ForeignKeyConstraint(['payment_method_id'], ['payment_methods.id'], name=op.f('payment_routing_rules_payment_method_id_fkey')),
    sa.ForeignKeyConstraint(['provider_id'], ['payment_providers.id'], name=op.f('payment_routing_rules_provider_id_fkey')),
    sa.ForeignKeyConstraint(['storefront_id'], ['storefronts.id'], name=op.f('payment_routing_rules_storefront_id_fkey')),
    sa.PrimaryKeyConstraint('id', name=op.f('payment_routing_rules_pkey'))
    )
    op.create_table('player_case_pity',
    sa.Column('account_id', sa.Uuid(), nullable=False),
    sa.Column('case_code', sa.String(length=64), nullable=False),
    sa.Column('rolls_since_guarantee', sa.Integer(), server_default='0', nullable=False),
    sa.Column('version', sa.BigInteger(), server_default='1', nullable=False),
    sa.CheckConstraint('rolls_since_guarantee >= 0 AND version > 0', name=op.f('ck_player_case_pity_counters')),
    sa.ForeignKeyConstraint(['case_code'], ['case_types.code'], name=op.f('player_case_pity_case_code_fkey')),
    sa.PrimaryKeyConstraint('account_id', 'case_code', name=op.f('player_case_pity_pkey'))
    )
    op.create_table('player_prestige_upgrades',
    sa.Column('account_id', sa.Uuid(), nullable=False),
    sa.Column('code', sa.String(length=64), nullable=False),
    sa.Column('level', sa.Integer(), nullable=False),
    sa.Column('updated_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.CheckConstraint('level > 0', name=op.f('ck_player_prestige_upgrades_level_positive')),
    sa.ForeignKeyConstraint(['code'], ['prestige_upgrade_types.code'], name=op.f('player_prestige_upgrades_code_fkey')),
    sa.PrimaryKeyConstraint('account_id', 'code', name=op.f('player_prestige_upgrades_pkey'))
    )
    op.create_table('provider_notification_inbox',
    sa.Column('provider_id', sa.Integer(), nullable=False),
    sa.Column('environment', sa.String(length=16), nullable=False),
    sa.Column('external_event_id', sa.String(length=255), nullable=True),
    sa.Column('payload_hash', sa.LargeBinary(), nullable=False),
    sa.Column('status', sa.String(length=32), server_default='PENDING', nullable=False),
    sa.Column('processed_at', sa.DateTime(timezone=True), nullable=True),
    sa.Column('id', sa.Uuid(), nullable=False),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.CheckConstraint("environment IN ('TEST','PRODUCTION')", name=op.f('ck_provider_notification_inbox_environment')),
    sa.CheckConstraint('octet_length(payload_hash) = 32', name=op.f('ck_provider_notification_inbox_hash_length')),
    sa.ForeignKeyConstraint(['provider_id'], ['payment_providers.id'], name=op.f('provider_notification_inbox_provider_id_fkey')),
    sa.PrimaryKeyConstraint('id', name=op.f('provider_notification_inbox_pkey')),
    sa.UniqueConstraint('provider_id', 'environment', 'external_event_id', name=op.f('provider_notification_inbox_provider_id_environment_external_event_id_key'))
    )
    op.create_table('reward_bundle_items',
    sa.Column('bundle_id', sa.Uuid(), nullable=False),
    sa.Column('item_index', sa.Integer(), nullable=False),
    sa.Column('reward_type', sa.String(length=32), nullable=False),
    sa.Column('reward_ref', sa.String(length=128), nullable=False),
    sa.Column('quantity', sa.BigInteger(), nullable=False),
    sa.CheckConstraint('item_index >= 0', name=op.f('ck_reward_bundle_items_index_nonnegative')),
    sa.CheckConstraint('quantity > 0', name=op.f('ck_reward_bundle_items_quantity_positive')),
    sa.ForeignKeyConstraint(['bundle_id'], ['reward_bundles.id'], name=op.f('reward_bundle_items_bundle_id_fkey')),
    sa.PrimaryKeyConstraint('bundle_id', 'item_index', name=op.f('reward_bundle_items_pkey'))
    )
    op.create_table('rulesets',
    sa.Column('id', sa.String(length=128), nullable=False),
    sa.Column('code', sa.String(length=128), nullable=False),
    sa.Column('version', sa.Integer(), server_default='1', nullable=False),
    sa.Column('content_release_id', sa.String(length=128), nullable=True),
    sa.Column('status', sa.String(length=16), server_default='DRAFT', nullable=False),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.CheckConstraint("status IN ('DRAFT','PUBLISHED','RETIRED')", name=op.f('ck_rulesets_status')),
    sa.CheckConstraint('version > 0', name=op.f('ck_rulesets_version_positive')),
    sa.ForeignKeyConstraint(['content_release_id'], ['content_releases.id'], name=op.f('rulesets_content_release_id_fkey')),
    sa.PrimaryKeyConstraint('id', name=op.f('rulesets_pkey')),
    sa.UniqueConstraint('code', 'version', name=op.f('rulesets_code_version_key'))
    )
    op.create_table('shop_products',
    sa.Column('internal_sku', sa.String(length=128), nullable=False),
    sa.Column('version', sa.Integer(), nullable=False),
    sa.Column('reward_bundle_id', sa.Uuid(), nullable=False),
    sa.Column('product_type', sa.String(length=32), nullable=False),
    sa.Column('enabled', sa.Boolean(), server_default='false', nullable=False),
    sa.Column('id', sa.Uuid(), nullable=False),
    sa.CheckConstraint("product_type IN ('CONSUMABLE','NON_CONSUMABLE','SUBSCRIPTION')", name=op.f('ck_shop_products_product_type')),
    sa.CheckConstraint('version > 0', name=op.f('ck_shop_products_version_positive')),
    sa.ForeignKeyConstraint(['reward_bundle_id'], ['reward_bundles.id'], name=op.f('shop_products_reward_bundle_id_fkey')),
    sa.PrimaryKeyConstraint('id', name=op.f('shop_products_pkey')),
    sa.UniqueConstraint('internal_sku', 'version', name=op.f('shop_products_internal_sku_version_key'))
    )
    op.create_table('case_definitions',
    sa.Column('code', sa.String(length=64), nullable=False),
    sa.Column('ruleset_id', sa.String(length=128), nullable=False),
    sa.Column('price_currency', sa.String(length=32), nullable=False),
    sa.Column('price', sa.BigInteger(), nullable=False),
    sa.Column('pity_threshold', sa.Integer(), nullable=False),
    sa.Column('guaranteed_rarity', sa.String(length=32), nullable=False),
    sa.Column('id', sa.Uuid(), nullable=False),
    sa.CheckConstraint('price >= 0 AND pity_threshold > 0', name=op.f('ck_case_definitions_price_and_pity')),
    sa.ForeignKeyConstraint(['code'], ['case_types.code'], name=op.f('case_definitions_code_fkey')),
    sa.ForeignKeyConstraint(['price_currency'], ['currencies.code'], name=op.f('case_definitions_price_currency_fkey')),
    sa.ForeignKeyConstraint(['ruleset_id'], ['rulesets.id'], name=op.f('case_definitions_ruleset_id_fkey')),
    sa.PrimaryKeyConstraint('id', name=op.f('case_definitions_pkey')),
    sa.UniqueConstraint('code', 'ruleset_id', name=op.f('case_definitions_code_ruleset_id_key'))
    )
    op.create_table('energy_configs',
    sa.Column('ruleset_id', sa.String(length=128), nullable=False),
    sa.Column('max_energy', sa.Integer(), nullable=False),
    sa.Column('regen_interval_ms', sa.BigInteger(), nullable=False),
    sa.Column('energy_per_tick', sa.Integer(), nullable=False),
    sa.CheckConstraint('energy_per_tick > 0', name=op.f('ck_energy_configs_tick_positive')),
    sa.CheckConstraint('max_energy > 0', name=op.f('ck_energy_configs_max_positive')),
    sa.CheckConstraint('regen_interval_ms > 0', name=op.f('ck_energy_configs_interval_positive')),
    sa.ForeignKeyConstraint(['ruleset_id'], ['rulesets.id'], name=op.f('energy_configs_ruleset_id_fkey')),
    sa.PrimaryKeyConstraint('ruleset_id', name=op.f('energy_configs_pkey'))
    )
    op.create_table('payment_orders',
    sa.Column('account_id', sa.Uuid(), nullable=False),
    sa.Column('product_id', sa.Uuid(), nullable=False),
    sa.Column('storefront_id', sa.Integer(), nullable=False),
    sa.Column('provider_id', sa.Integer(), nullable=False),
    sa.Column('environment', sa.String(length=16), nullable=False),
    sa.Column('country_iso', sa.String(length=2), nullable=True),
    sa.Column('app_package', sa.String(length=255), nullable=True),
    sa.Column('client_request_key', sa.Uuid(), nullable=False),
    sa.Column('currency_iso', sa.String(length=3), nullable=False),
    sa.Column('price_minor', sa.BigInteger(), nullable=False),
    sa.Column('status', sa.String(length=16), server_default='PENDING', nullable=False),
    sa.Column('id', sa.Uuid(), nullable=False),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.CheckConstraint("environment IN ('TEST','PRODUCTION')", name=op.f('ck_payment_orders_environment')),
    sa.CheckConstraint("status IN ('PENDING','UNKNOWN','VERIFIED','DECLINED','FULFILLED','REFUNDED','CANCELLED')", name=op.f('ck_payment_orders_status')),
    sa.CheckConstraint('price_minor >= 0', name=op.f('ck_payment_orders_price_nonnegative')),
    sa.ForeignKeyConstraint(['product_id'], ['shop_products.id'], name=op.f('payment_orders_product_id_fkey')),
    sa.ForeignKeyConstraint(['provider_id'], ['payment_providers.id'], name=op.f('payment_orders_provider_id_fkey')),
    sa.ForeignKeyConstraint(['storefront_id'], ['storefronts.id'], name=op.f('payment_orders_storefront_id_fkey')),
    sa.PrimaryKeyConstraint('id', name=op.f('payment_orders_pkey')),
    sa.UniqueConstraint('account_id', 'client_request_key', name=op.f('payment_orders_account_id_client_request_key_key')),
    sa.UniqueConstraint('id', 'provider_id', 'environment', name='uq_payment_order_provider_environment')
    )
    op.create_index(op.f('ix_payment_orders_account_id'), 'payment_orders', ['account_id'], unique=False)
    op.create_table('player_level_definitions',
    sa.Column('ruleset_id', sa.String(length=128), nullable=False),
    sa.Column('level', sa.Integer(), nullable=False),
    sa.Column('total_xp_required', sa.BigInteger(), nullable=False),
    sa.Column('reward_bundle_id', sa.Uuid(), nullable=True),
    sa.CheckConstraint('level > 0', name=op.f('ck_player_level_definitions_level_positive')),
    sa.CheckConstraint('total_xp_required >= 0', name=op.f('ck_player_level_definitions_xp_nonnegative')),
    sa.ForeignKeyConstraint(['reward_bundle_id'], ['reward_bundles.id'], name=op.f('player_level_definitions_reward_bundle_id_fkey')),
    sa.ForeignKeyConstraint(['ruleset_id'], ['rulesets.id'], name=op.f('player_level_definitions_ruleset_id_fkey')),
    sa.PrimaryKeyConstraint('ruleset_id', 'level', name=op.f('player_level_definitions_pkey')),
    sa.UniqueConstraint('ruleset_id', 'level', 'reward_bundle_id', name='uq_level_definition_reward'),
    sa.UniqueConstraint('ruleset_id', 'total_xp_required', name=op.f('player_level_definitions_ruleset_id_total_xp_required_key'))
    )
    op.create_table('prestige_reward_rules',
    sa.Column('ruleset_id', sa.String(length=128), nullable=False),
    sa.Column('church_level', sa.Integer(), nullable=False),
    sa.Column('reward', sa.BigInteger(), nullable=False),
    sa.CheckConstraint('church_level > 0', name=op.f('ck_prestige_reward_rules_level_positive')),
    sa.CheckConstraint('reward >= 0', name=op.f('ck_prestige_reward_rules_reward_nonnegative')),
    sa.ForeignKeyConstraint(['ruleset_id'], ['rulesets.id'], name=op.f('prestige_reward_rules_ruleset_id_fkey')),
    sa.PrimaryKeyConstraint('ruleset_id', 'church_level', name=op.f('prestige_reward_rules_pkey'))
    )
    op.create_table('prestige_upgrade_definitions',
    sa.Column('ruleset_id', sa.String(length=128), nullable=False),
    sa.Column('code', sa.String(length=64), nullable=False),
    sa.Column('level', sa.Integer(), nullable=False),
    sa.Column('target', sa.String(length=64), nullable=False),
    sa.Column('stat', sa.String(length=64), nullable=False),
    sa.Column('modifier_value', sa.Numeric(precision=18, scale=6), nullable=False),
    sa.Column('cost', sa.BigInteger(), nullable=False),
    sa.CheckConstraint('cost >= 0', name=op.f('ck_prestige_upgrade_definitions_cost_nonnegative')),
    sa.CheckConstraint('level > 0', name=op.f('ck_prestige_upgrade_definitions_level_positive')),
    sa.ForeignKeyConstraint(['code'], ['prestige_upgrade_types.code'], name=op.f('prestige_upgrade_definitions_code_fkey')),
    sa.ForeignKeyConstraint(['ruleset_id'], ['rulesets.id'], name=op.f('prestige_upgrade_definitions_ruleset_id_fkey')),
    sa.PrimaryKeyConstraint('ruleset_id', 'code', 'level', name=op.f('prestige_upgrade_definitions_pkey'))
    )
    op.create_table('ruleset_limits',
    sa.Column('ruleset_id', sa.String(length=128), nullable=False),
    sa.Column('rule_key', sa.String(length=128), nullable=False),
    sa.Column('target_key', sa.String(length=128), server_default='*', nullable=False),
    sa.Column('minimum', sa.Numeric(precision=24, scale=6), nullable=True),
    sa.Column('maximum', sa.Numeric(precision=24, scale=6), nullable=True),
    sa.CheckConstraint('minimum <= maximum', name=op.f('ck_ruleset_limits_bounds_order')),
    sa.CheckConstraint('minimum IS NOT NULL OR maximum IS NOT NULL', name=op.f('ck_ruleset_limits_bound_required')),
    sa.ForeignKeyConstraint(['ruleset_id'], ['rulesets.id'], name=op.f('ruleset_limits_ruleset_id_fkey')),
    sa.PrimaryKeyConstraint('ruleset_id', 'rule_key', 'target_key', name=op.f('ruleset_limits_pkey'))
    )
    op.create_table('runs',
    sa.Column('account_id', sa.Uuid(), nullable=False),
    sa.Column('server_id', sa.Uuid(), nullable=True),
    sa.Column('simulation_mode', sa.String(length=16), server_default='DEDICATED', nullable=False),
    sa.Column('ruleset_id', sa.String(length=128), nullable=False),
    sa.Column('content_release_id', sa.String(length=128), nullable=False),
    sa.Column('profile_version', sa.Integer(), nullable=False),
    sa.Column('profile_display_name', sa.String(length=32), nullable=False),
    sa.Column('generation', sa.Integer(), nullable=False),
    sa.Column('status', sa.String(length=16), nullable=False),
    sa.Column('completed_at', sa.DateTime(timezone=True), nullable=True),
    sa.Column('outcome', sa.String(length=16), nullable=True),
    sa.Column('permanent_reward', sa.BigInteger(), server_default='0', nullable=False),
    sa.Column('reward_per_church_level', sa.BigInteger(), nullable=True),
    sa.Column('weapon_snapshot', postgresql.JSONB(astext_type=sa.Text()), nullable=True),
    sa.Column('id', sa.Uuid(), nullable=False),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.CheckConstraint("(simulation_mode = 'DEDICATED' AND server_id IS NOT NULL) OR (simulation_mode = 'LOCAL' AND server_id IS NULL)", name=op.f('ck_runs_simulation_mode')),
    sa.CheckConstraint("status IN ('pending', 'ready', 'failed', 'completed')", name=op.f('ck_runs_status')),
    sa.CheckConstraint('permanent_reward >= 0', name=op.f('ck_runs_reward_nonnegative')),
    sa.CheckConstraint('reward_per_church_level >= 0', name=op.f('ck_runs_reward_rate_nonnegative')),
    sa.ForeignKeyConstraint(['ruleset_id'], ['rulesets.id'], name=op.f('runs_ruleset_id_fkey')),
    sa.ForeignKeyConstraint(['server_id'], ['game_servers.id'], name=op.f('runs_server_id_fkey')),
    sa.PrimaryKeyConstraint('id', name=op.f('runs_pkey')),
    sa.UniqueConstraint('id', 'account_id', name=op.f('runs_id_account_id_key')),
    sa.UniqueConstraint('id', 'ruleset_id', name=op.f('runs_id_ruleset_id_key'))
    )
    op.create_index('runs_account_idx', 'runs', ['account_id', sa.literal_column('created_at DESC')], unique=False)
    op.create_index('runs_live_account_idx', 'runs', ['account_id'], unique=True, postgresql_where=sa.text("status IN ('pending', 'ready')"))
    op.create_index('runs_live_server_idx', 'runs', ['server_id'], unique=True, postgresql_where=sa.text("status IN ('pending', 'ready')"))
    op.create_table('store_product_mappings',
    sa.Column('product_id', sa.Uuid(), nullable=False),
    sa.Column('storefront_id', sa.Integer(), nullable=False),
    sa.Column('provider_id', sa.Integer(), nullable=False),
    sa.Column('external_sku', sa.String(length=255), nullable=False),
    sa.Column('environment', sa.String(length=16), nullable=False),
    sa.Column('id', sa.Uuid(), nullable=False),
    sa.CheckConstraint("environment IN ('TEST','PRODUCTION')", name=op.f('ck_store_product_mappings_environment')),
    sa.ForeignKeyConstraint(['product_id'], ['shop_products.id'], name=op.f('store_product_mappings_product_id_fkey')),
    sa.ForeignKeyConstraint(['provider_id'], ['payment_providers.id'], name=op.f('store_product_mappings_provider_id_fkey')),
    sa.ForeignKeyConstraint(['storefront_id'], ['storefronts.id'], name=op.f('store_product_mappings_storefront_id_fkey')),
    sa.PrimaryKeyConstraint('id', name=op.f('store_product_mappings_pkey')),
    sa.UniqueConstraint('provider_id', 'storefront_id', 'environment', 'external_sku', name=op.f('store_product_mappings_provider_id_storefront_id_environment_external_sku_key'))
    )
    op.create_table('weapon_loadouts',
    sa.Column('account_id', sa.Uuid(), nullable=False),
    sa.Column('slot', sa.Integer(), nullable=False),
    sa.Column('instance_id', sa.Uuid(), nullable=False),
    sa.CheckConstraint('slot BETWEEN 0 AND 2', name=op.f('ck_weapon_loadouts_slot_range')),
    sa.ForeignKeyConstraint(['instance_id', 'account_id'], ['owned_weapons.instance_id', 'owned_weapons.account_id'], name='fk_weapon_loadout_owner'),
    sa.ForeignKeyConstraint(['instance_id'], ['owned_weapons.instance_id'], name=op.f('weapon_loadouts_instance_id_fkey')),
    sa.PrimaryKeyConstraint('account_id', 'slot', name=op.f('weapon_loadouts_pkey')),
    sa.UniqueConstraint('account_id', 'instance_id', name='weapon_loadouts_account_id_instance_id_key')
    )
    op.create_table('xp_reward_rules',
    sa.Column('ruleset_id', sa.String(length=128), nullable=False),
    sa.Column('source_type', sa.String(length=32), nullable=False),
    sa.Column('target_key', sa.String(length=128), nullable=False),
    sa.Column('xp_per_unit', sa.Integer(), nullable=False),
    sa.Column('max_xp_per_run', sa.BigInteger(), nullable=False),
    sa.Column('id', sa.Uuid(), nullable=False),
    sa.CheckConstraint("source_type IN ('HERO_KILL','TOWER_KILL','BUILDING_UPGRADE','RUN_VICTORY')", name=op.f('ck_xp_reward_rules_source_type')),
    sa.CheckConstraint('xp_per_unit >= 0 AND max_xp_per_run >= 0', name=op.f('ck_xp_reward_rules_xp_nonnegative')),
    sa.ForeignKeyConstraint(['ruleset_id'], ['rulesets.id'], name=op.f('xp_reward_rules_ruleset_id_fkey')),
    sa.PrimaryKeyConstraint('id', name=op.f('xp_reward_rules_pkey')),
    sa.UniqueConstraint('ruleset_id', 'source_type', 'target_key', name=op.f('xp_reward_rules_ruleset_id_source_type_target_key_key'))
    )
    op.create_table('case_drop_entries',
    sa.Column('case_id', sa.Uuid(), nullable=False),
    sa.Column('entry_index', sa.Integer(), nullable=False),
    sa.Column('reward_bundle_id', sa.Uuid(), nullable=False),
    sa.Column('rarity', sa.String(length=32), nullable=False),
    sa.Column('weight', sa.BigInteger(), nullable=False),
    sa.CheckConstraint('entry_index >= 0 AND weight > 0', name=op.f('ck_case_drop_entries_entry_weight')),
    sa.ForeignKeyConstraint(['case_id'], ['case_definitions.id'], name=op.f('case_drop_entries_case_id_fkey')),
    sa.ForeignKeyConstraint(['reward_bundle_id'], ['reward_bundles.id'], name=op.f('case_drop_entries_reward_bundle_id_fkey')),
    sa.PrimaryKeyConstraint('case_id', 'entry_index', name=op.f('case_drop_entries_pkey'))
    )
    op.create_table('case_openings',
    sa.Column('account_id', sa.Uuid(), nullable=False),
    sa.Column('case_id', sa.Uuid(), nullable=False),
    sa.Column('client_request_key', sa.Uuid(), nullable=False),
    sa.Column('reward_bundle_id', sa.Uuid(), nullable=False),
    sa.Column('rarity', sa.String(length=32), nullable=False),
    sa.Column('pity_before', sa.Integer(), nullable=False),
    sa.Column('pity_after', sa.Integer(), nullable=False),
    sa.Column('result', postgresql.JSONB(astext_type=sa.Text()), nullable=False),
    sa.Column('id', sa.Uuid(), nullable=False),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.CheckConstraint('pity_before >= 0 AND pity_after >= 0', name=op.f('ck_case_openings_pity_nonnegative')),
    sa.ForeignKeyConstraint(['case_id'], ['case_definitions.id'], name=op.f('case_openings_case_id_fkey')),
    sa.ForeignKeyConstraint(['reward_bundle_id'], ['reward_bundles.id'], name=op.f('case_openings_reward_bundle_id_fkey')),
    sa.PrimaryKeyConstraint('id', name=op.f('case_openings_pkey')),
    sa.UniqueConstraint('account_id', 'client_request_key', name=op.f('case_openings_account_id_client_request_key_key'))
    )
    op.create_table('payment_attempts',
    sa.Column('order_id', sa.Uuid(), nullable=False),
    sa.Column('external_payment_id', sa.String(length=255), nullable=True),
    sa.Column('status', sa.String(length=32), nullable=False),
    sa.Column('id', sa.Uuid(), nullable=False),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.ForeignKeyConstraint(['order_id'], ['payment_orders.id'], name=op.f('payment_attempts_order_id_fkey')),
    sa.PrimaryKeyConstraint('id', name=op.f('payment_attempts_pkey'))
    )
    op.create_index(op.f('ix_payment_attempts_order_id'), 'payment_attempts', ['order_id'], unique=False)
    op.create_table('player_energy',
    sa.Column('account_id', sa.Uuid(), nullable=False),
    sa.Column('ruleset_id', sa.String(length=128), nullable=False),
    sa.Column('current_energy', sa.Integer(), nullable=False),
    sa.Column('last_calculated_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.Column('regen_progress_ms', sa.BigInteger(), server_default='0', nullable=False),
    sa.Column('state_version', sa.BigInteger(), server_default='1', nullable=False),
    sa.CheckConstraint('current_energy >= 0', name=op.f('ck_player_energy_energy_nonnegative')),
    sa.CheckConstraint('regen_progress_ms >= 0', name=op.f('ck_player_energy_progress_nonnegative')),
    sa.CheckConstraint('state_version > 0', name=op.f('ck_player_energy_version_positive')),
    sa.ForeignKeyConstraint(['ruleset_id'], ['energy_configs.ruleset_id'], name=op.f('player_energy_ruleset_id_fkey')),
    sa.PrimaryKeyConstraint('account_id', name=op.f('player_energy_pkey'))
    )
    op.create_table('player_level_reward_claims',
    sa.Column('account_id', sa.Uuid(), nullable=False),
    sa.Column('rewarded_level', sa.Integer(), nullable=False),
    sa.Column('ruleset_id', sa.String(length=128), nullable=False),
    sa.Column('reward_bundle_id', sa.Uuid(), nullable=False),
    sa.Column('status', sa.String(length=16), server_default='AVAILABLE', nullable=False),
    sa.Column('claimed_at', sa.DateTime(timezone=True), nullable=True),
    sa.Column('id', sa.Uuid(), nullable=False),
    sa.CheckConstraint("(status = 'AVAILABLE' AND claimed_at IS NULL) OR (status = 'CLAIMED' AND claimed_at IS NOT NULL)", name=op.f('ck_player_level_reward_claims_claim_state')),
    sa.CheckConstraint('rewarded_level > 0', name=op.f('ck_player_level_reward_claims_level_positive')),
    sa.ForeignKeyConstraint(['reward_bundle_id'], ['reward_bundles.id'], name=op.f('player_level_reward_claims_reward_bundle_id_fkey')),
    sa.ForeignKeyConstraint(['ruleset_id', 'rewarded_level', 'reward_bundle_id'], ['player_level_definitions.ruleset_id', 'player_level_definitions.level', 'player_level_definitions.reward_bundle_id'], name='fk_level_claim_definition_reward'),
    sa.ForeignKeyConstraint(['ruleset_id'], ['rulesets.id'], name=op.f('player_level_reward_claims_ruleset_id_fkey')),
    sa.PrimaryKeyConstraint('id', name=op.f('player_level_reward_claims_pkey')),
    sa.UniqueConstraint('account_id', 'rewarded_level', name=op.f('player_level_reward_claims_account_id_rewarded_level_key')),
    sa.UniqueConstraint('id', 'reward_bundle_id', name=op.f('player_level_reward_claims_id_reward_bundle_id_key'))
    )
    op.create_table('purchase_transactions',
    sa.Column('order_id', sa.Uuid(), nullable=False),
    sa.Column('provider_id', sa.Integer(), nullable=False),
    sa.Column('environment', sa.String(length=16), nullable=False),
    sa.Column('external_transaction_id', sa.String(length=255), nullable=False),
    sa.Column('verification_status', sa.String(length=16), nullable=False),
    sa.Column('verified_at', sa.DateTime(timezone=True), nullable=True),
    sa.Column('refunded_at', sa.DateTime(timezone=True), nullable=True),
    sa.Column('id', sa.Uuid(), nullable=False),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.CheckConstraint("environment IN ('TEST','PRODUCTION')", name=op.f('ck_purchase_transactions_environment')),
    sa.CheckConstraint("verification_status IN ('PENDING','VERIFIED','REJECTED','REFUNDED')", name=op.f('ck_purchase_transactions_status')),
    sa.ForeignKeyConstraint(['order_id', 'provider_id', 'environment'], ['payment_orders.id', 'payment_orders.provider_id', 'payment_orders.environment'], name='fk_payment_transaction_order_provider'),
    sa.ForeignKeyConstraint(['order_id'], ['payment_orders.id'], name=op.f('purchase_transactions_order_id_fkey')),
    sa.ForeignKeyConstraint(['provider_id'], ['payment_providers.id'], name=op.f('purchase_transactions_provider_id_fkey')),
    sa.PrimaryKeyConstraint('id', name=op.f('purchase_transactions_pkey')),
    sa.UniqueConstraint('provider_id', 'environment', 'external_transaction_id', name=op.f('purchase_transactions_provider_id_environment_external_transaction_id_key'))
    )
    op.create_index(op.f('ix_purchase_transactions_order_id'), 'purchase_transactions', ['order_id'], unique=False)
    op.create_table('run_checkpoints',
    sa.Column('run_id', sa.Uuid(), nullable=False),
    sa.Column('generation', sa.Integer(), nullable=False),
    sa.Column('schema_version', sa.Integer(), nullable=False),
    sa.Column('simulation_tick', sa.BigInteger(), nullable=False),
    sa.Column('snapshot_json', sa.Text(), nullable=False),
    sa.Column('sha256', sa.LargeBinary(), nullable=False),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.CheckConstraint('generation > 0', name=op.f('ck_run_checkpoints_generation_positive')),
    sa.CheckConstraint('octet_length(sha256) = 32', name=op.f('ck_run_checkpoints_sha256_length')),
    sa.CheckConstraint('schema_version > 0', name=op.f('ck_run_checkpoints_schema_positive')),
    sa.CheckConstraint('simulation_tick >= 0', name=op.f('ck_run_checkpoints_tick_nonnegative')),
    sa.ForeignKeyConstraint(['run_id'], ['runs.id'], name=op.f('run_checkpoints_run_id_fkey')),
    sa.PrimaryKeyConstraint('run_id', name=op.f('run_checkpoints_pkey'))
    )
    op.create_table('run_contexts',
    sa.Column('run_id', sa.Uuid(), nullable=False),
    sa.Column('account_id', sa.Uuid(), nullable=False),
    sa.Column('session_id', sa.Uuid(), nullable=False),
    sa.Column('client_request_key', sa.Uuid(), nullable=False),
    sa.Column('map_id', sa.String(length=128), nullable=False),
    sa.Column('difficulty', sa.String(length=32), nullable=False),
    sa.Column('ruleset_id', sa.String(length=128), nullable=False),
    sa.Column('energy_cost', sa.Integer(), nullable=False),
    sa.Column('started_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.Column('submission_deadline', sa.DateTime(timezone=True), nullable=False),
    sa.CheckConstraint('energy_cost >= 0', name=op.f('ck_run_contexts_energy_nonnegative')),
    sa.CheckConstraint('submission_deadline > started_at', name=op.f('ck_run_contexts_deadline_after_start')),
    sa.ForeignKeyConstraint(['ruleset_id'], ['rulesets.id'], name=op.f('run_contexts_ruleset_id_fkey')),
    sa.ForeignKeyConstraint(['run_id', 'account_id'], ['runs.id', 'runs.account_id'], name='fk_run_context_owner'),
    sa.ForeignKeyConstraint(['run_id', 'ruleset_id'], ['runs.id', 'runs.ruleset_id'], name='fk_run_context_ruleset'),
    sa.ForeignKeyConstraint(['run_id'], ['runs.id'], name=op.f('run_contexts_run_id_fkey')),
    sa.PrimaryKeyConstraint('run_id', name=op.f('run_contexts_pkey')),
    sa.UniqueConstraint('account_id', 'client_request_key', name=op.f('run_contexts_account_id_client_request_key_key'))
    )
    op.create_table('run_result_operations',
    sa.Column('run_id', sa.Uuid(), nullable=False),
    sa.Column('account_id', sa.Uuid(), nullable=False),
    sa.Column('server_id', sa.Uuid(), nullable=False),
    sa.Column('generation', sa.Integer(), nullable=False),
    sa.Column('content_release_id', sa.String(length=128), nullable=False),
    sa.Column('payload_hash', sa.LargeBinary(), nullable=False),
    sa.Column('outcome', sa.String(length=16), nullable=False),
    sa.Column('permanent_reward', sa.BigInteger(), nullable=False),
    sa.Column('profile_version', sa.Integer(), nullable=False),
    sa.Column('church_level', sa.Integer(), server_default='1', nullable=False),
    sa.Column('applied_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.CheckConstraint('church_level BETWEEN 1 AND 1000', name=op.f('ck_run_result_operations_church_level_range')),
    sa.CheckConstraint('permanent_reward >= 0', name=op.f('ck_run_result_operations_reward_nonnegative')),
    sa.CheckConstraint('profile_version > 0', name=op.f('ck_run_result_operations_version_positive')),
    sa.ForeignKeyConstraint(['run_id'], ['runs.id'], name=op.f('run_result_operations_run_id_fkey')),
    sa.ForeignKeyConstraint(['server_id'], ['game_servers.id'], name=op.f('run_result_operations_server_id_fkey')),
    sa.PrimaryKeyConstraint('run_id', name=op.f('run_result_operations_pkey'))
    )
    op.create_table('run_result_submissions',
    sa.Column('run_id', sa.Uuid(), nullable=False),
    sa.Column('payload_hash', sa.LargeBinary(), nullable=False),
    sa.Column('schema_version', sa.Integer(), nullable=False),
    sa.Column('snapshot', postgresql.JSONB(astext_type=sa.Text()), nullable=False),
    sa.Column('validation_status', sa.String(length=16), nullable=False),
    sa.Column('validator_version', sa.String(length=64), nullable=False),
    sa.Column('validation_reasons', postgresql.JSONB(astext_type=sa.Text()), server_default='[]', nullable=False),
    sa.Column('settlement', postgresql.JSONB(astext_type=sa.Text()), nullable=True),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.CheckConstraint("validation_status = 'ACCEPTED' OR settlement IS NULL", name=op.f('ck_run_result_submissions_accepted_settlement')),
    sa.CheckConstraint("validation_status IN ('ACCEPTED','REVIEW','REJECTED')", name=op.f('ck_run_result_submissions_validation_status')),
    sa.CheckConstraint('octet_length(payload_hash) = 32', name=op.f('ck_run_result_submissions_hash_length')),
    sa.CheckConstraint('schema_version > 0', name=op.f('ck_run_result_submissions_schema_positive')),
    sa.ForeignKeyConstraint(['run_id'], ['runs.id'], name=op.f('run_result_submissions_run_id_fkey')),
    sa.PrimaryKeyConstraint('run_id', name=op.f('run_result_submissions_pkey'))
    )
    op.create_table('run_reward_grants',
    sa.Column('run_id', sa.Uuid(), nullable=False),
    sa.Column('account_id', sa.Uuid(), nullable=False),
    sa.Column('transaction_id', sa.Uuid(), nullable=False),
    sa.Column('ruleset_id', sa.String(), nullable=False),
    sa.Column('church_level', sa.Integer(), nullable=False),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.ForeignKeyConstraint(['ruleset_id', 'church_level'], ['prestige_reward_rules.ruleset_id', 'prestige_reward_rules.church_level'], name=op.f('run_reward_grants_ruleset_id_fkey')),
    sa.ForeignKeyConstraint(['run_id', 'account_id'], ['runs.id', 'runs.account_id'], name='fk_run_reward_owner'),
    sa.ForeignKeyConstraint(['run_id', 'ruleset_id'], ['runs.id', 'runs.ruleset_id'], name='fk_run_reward_ruleset'),
    sa.ForeignKeyConstraint(['run_id'], ['runs.id'], name=op.f('run_reward_grants_run_id_fkey')),
    sa.ForeignKeyConstraint(['transaction_id', 'account_id'], ['economy_transactions.id', 'economy_transactions.account_id'], name='fk_run_reward_ledger_owner'),
    sa.ForeignKeyConstraint(['transaction_id'], ['economy_transactions.id'], name=op.f('run_reward_grants_transaction_id_fkey')),
    sa.PrimaryKeyConstraint('run_id', name=op.f('run_reward_grants_pkey')),
    sa.UniqueConstraint('transaction_id', name=op.f('run_reward_grants_transaction_id_key'))
    )
    op.create_table('run_xp_breakdowns',
    sa.Column('run_id', sa.Uuid(), nullable=False),
    sa.Column('ruleset_id', sa.String(length=128), nullable=False),
    sa.Column('hero_kill_xp', sa.BigInteger(), nullable=False),
    sa.Column('tower_kill_xp', sa.BigInteger(), nullable=False),
    sa.Column('tycoon_xp', sa.BigInteger(), nullable=False),
    sa.Column('victory_xp', sa.BigInteger(), nullable=False),
    sa.Column('total_xp', sa.BigInteger(), nullable=False),
    sa.CheckConstraint('hero_kill_xp >= 0 AND tower_kill_xp >= 0 AND tycoon_xp >= 0 AND victory_xp >= 0', name=op.f('ck_run_xp_breakdowns_xp_nonnegative')),
    sa.CheckConstraint('total_xp = hero_kill_xp + tower_kill_xp + tycoon_xp + victory_xp', name=op.f('ck_run_xp_breakdowns_xp_total')),
    sa.ForeignKeyConstraint(['ruleset_id'], ['rulesets.id'], name=op.f('run_xp_breakdowns_ruleset_id_fkey')),
    sa.ForeignKeyConstraint(['run_id', 'ruleset_id'], ['runs.id', 'runs.ruleset_id'], name='fk_run_xp_ruleset'),
    sa.ForeignKeyConstraint(['run_id'], ['runs.id'], name=op.f('run_xp_breakdowns_run_id_fkey')),
    sa.PrimaryKeyConstraint('run_id', name=op.f('run_xp_breakdowns_pkey'))
    )
    op.create_table('entitlement_grants',
    sa.Column('transaction_id', sa.Uuid(), nullable=False),
    sa.Column('reward_line_index', sa.Integer(), nullable=False),
    sa.Column('reward_type', sa.String(length=32), nullable=False),
    sa.Column('reward_ref', sa.String(length=128), nullable=False),
    sa.Column('amount', sa.BigInteger(), nullable=False),
    sa.Column('revoked_at', sa.DateTime(timezone=True), nullable=True),
    sa.Column('id', sa.Uuid(), nullable=False),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.CheckConstraint('amount > 0', name=op.f('ck_entitlement_grants_amount_positive')),
    sa.CheckConstraint('reward_line_index >= 0', name=op.f('ck_entitlement_grants_line_nonnegative')),
    sa.ForeignKeyConstraint(['transaction_id'], ['purchase_transactions.id'], name=op.f('entitlement_grants_transaction_id_fkey')),
    sa.PrimaryKeyConstraint('id', name=op.f('entitlement_grants_pkey')),
    sa.UniqueConstraint('transaction_id', 'reward_line_index', name=op.f('entitlement_grants_transaction_id_reward_line_index_key'))
    )
    op.create_table('level_reward_grants',
    sa.Column('claim_id', sa.Uuid(), nullable=False),
    sa.Column('item_index', sa.Integer(), nullable=False),
    sa.Column('bundle_id', sa.Uuid(), nullable=False),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.ForeignKeyConstraint(['bundle_id', 'item_index'], ['reward_bundle_items.bundle_id', 'reward_bundle_items.item_index'], name=op.f('level_reward_grants_bundle_id_fkey')),
    sa.ForeignKeyConstraint(['claim_id', 'bundle_id'], ['player_level_reward_claims.id', 'player_level_reward_claims.reward_bundle_id'], name=op.f('level_reward_grants_claim_id_fkey')),
    sa.PrimaryKeyConstraint('claim_id', 'item_index', name=op.f('level_reward_grants_pkey'))
    )
    op.create_table('outbox_messages',
    sa.Column('destination', sa.String(length=64), nullable=False),
    sa.Column('event_name', sa.String(length=128), nullable=False),
    sa.Column('schema_version', sa.Integer(), nullable=False),
    sa.Column('aggregate_id', sa.Uuid(), nullable=False),
    sa.Column('payment_transaction_id', sa.Uuid(), nullable=True),
    sa.Column('payload', postgresql.JSONB(astext_type=sa.Text()), nullable=False),
    sa.Column('available_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.Column('published_at', sa.DateTime(timezone=True), nullable=True),
    sa.Column('attempts', sa.Integer(), server_default='0', nullable=False),
    sa.Column('last_error', sa.Text(), nullable=True),
    sa.Column('id', sa.Uuid(), nullable=False),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.CheckConstraint('payment_transaction_id IS NULL OR aggregate_id = payment_transaction_id', name=op.f('ck_outbox_messages_payment_aggregate')),
    sa.CheckConstraint('schema_version > 0 AND attempts >= 0', name=op.f('ck_outbox_messages_counters')),
    sa.ForeignKeyConstraint(['payment_transaction_id'], ['purchase_transactions.id'], name=op.f('outbox_messages_payment_transaction_id_fkey')),
    sa.PrimaryKeyConstraint('id', name=op.f('outbox_messages_pkey'))
    )
    op.create_index('ix_outbox_pending', 'outbox_messages', ['available_at', 'created_at'], unique=False, postgresql_where=sa.text('published_at IS NULL'))
    op.create_table('player_xp_transactions',
    sa.Column('account_id', sa.Uuid(), nullable=False),
    sa.Column('run_id', sa.Uuid(), nullable=False),
    sa.Column('xp_delta', sa.BigInteger(), nullable=False),
    sa.Column('id', sa.Uuid(), nullable=False),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.CheckConstraint('xp_delta >= 0', name=op.f('ck_player_xp_transactions_xp_nonnegative')),
    sa.ForeignKeyConstraint(['run_id', 'account_id'], ['runs.id', 'runs.account_id'], name='fk_xp_transaction_run_owner'),
    sa.ForeignKeyConstraint(['run_id'], ['run_xp_breakdowns.run_id'], name=op.f('player_xp_transactions_run_id_fkey')),
    sa.PrimaryKeyConstraint('id', name=op.f('player_xp_transactions_pkey')),
    sa.UniqueConstraint('run_id', name=op.f('player_xp_transactions_run_id_key'))
    )
    op.create_index(op.f('ix_player_xp_transactions_account_id'), 'player_xp_transactions', ['account_id'], unique=False)
    op.create_table('run_validation_audits',
    sa.Column('run_id', sa.Uuid(), nullable=False),
    sa.Column('ruleset_id', sa.String(length=128), nullable=False),
    sa.Column('validator_version', sa.String(length=64), nullable=False),
    sa.Column('status', sa.String(length=16), nullable=False),
    sa.Column('reasons', postgresql.JSONB(astext_type=sa.Text()), server_default='[]', nullable=False),
    sa.Column('id', sa.Uuid(), nullable=False),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.CheckConstraint("status IN ('ACCEPTED','REVIEW','REJECTED')", name=op.f('ck_run_validation_audits_status')),
    sa.ForeignKeyConstraint(['ruleset_id'], ['rulesets.id'], name=op.f('run_validation_audits_ruleset_id_fkey')),
    sa.ForeignKeyConstraint(['run_id'], ['run_result_submissions.run_id'], name=op.f('run_validation_audits_run_id_fkey')),
    sa.PrimaryKeyConstraint('id', name=op.f('run_validation_audits_pkey'))
    )
    op.create_index(op.f('ix_run_validation_audits_run_id'), 'run_validation_audits', ['run_id'], unique=False)
    op.create_foreign_key('game_servers_current_run_fk', 'game_servers', 'runs', ['current_run_id'], ['id'])


def downgrade() -> None:
    op.drop_constraint('game_servers_current_run_fk', 'game_servers', type_='foreignkey')
    op.drop_index(op.f('ix_run_validation_audits_run_id'), table_name='run_validation_audits')
    op.drop_table('run_validation_audits')
    op.drop_index(op.f('ix_player_xp_transactions_account_id'), table_name='player_xp_transactions')
    op.drop_table('player_xp_transactions')
    op.drop_index('ix_outbox_pending', table_name='outbox_messages', postgresql_where=sa.text('published_at IS NULL'))
    op.drop_table('outbox_messages')
    op.drop_table('level_reward_grants')
    op.drop_table('entitlement_grants')
    op.drop_table('run_xp_breakdowns')
    op.drop_table('run_reward_grants')
    op.drop_table('run_result_submissions')
    op.drop_table('run_result_operations')
    op.drop_table('run_contexts')
    op.drop_table('run_checkpoints')
    op.drop_index(op.f('ix_purchase_transactions_order_id'), table_name='purchase_transactions')
    op.drop_table('purchase_transactions')
    op.drop_table('player_level_reward_claims')
    op.drop_table('player_energy')
    op.drop_index(op.f('ix_payment_attempts_order_id'), table_name='payment_attempts')
    op.drop_table('payment_attempts')
    op.drop_table('case_openings')
    op.drop_table('case_drop_entries')
    op.drop_table('xp_reward_rules')
    op.drop_table('weapon_loadouts')
    op.drop_table('store_product_mappings')
    op.drop_index('runs_live_server_idx', table_name='runs', postgresql_where=sa.text("status IN ('pending', 'ready')"))
    op.drop_index('runs_live_account_idx', table_name='runs', postgresql_where=sa.text("status IN ('pending', 'ready')"))
    op.drop_index('runs_account_idx', table_name='runs')
    op.drop_table('runs')
    op.drop_table('ruleset_limits')
    op.drop_table('prestige_upgrade_definitions')
    op.drop_table('prestige_reward_rules')
    op.drop_table('player_level_definitions')
    op.drop_index(op.f('ix_payment_orders_account_id'), table_name='payment_orders')
    op.drop_table('payment_orders')
    op.drop_table('energy_configs')
    op.drop_table('case_definitions')
    op.drop_table('shop_products')
    op.drop_table('rulesets')
    op.drop_table('reward_bundle_items')
    op.drop_table('provider_notification_inbox')
    op.drop_table('player_prestige_upgrades')
    op.drop_table('player_case_pity')
    op.drop_table('payment_routing_rules')
    op.drop_index('owned_weapons_account_idx', table_name='owned_weapons')
    op.drop_table('owned_weapons')
    op.drop_index('ix_leaderboard_scores_ranking', table_name='leaderboard_scores')
    op.drop_table('leaderboard_scores')
    op.drop_index(op.f('ix_economy_transactions_account_id'), table_name='economy_transactions')
    op.drop_table('economy_transactions')
    op.drop_table('currency_balances')
    op.drop_table('weapon_configurations')
    op.drop_table('storefronts')
    op.drop_table('reward_bundles')
    op.drop_index('purchase_operations_completed_offer_idx', table_name='purchase_operations', postgresql_where=sa.text("receipt_json->>'status'='Completed'"))
    op.drop_table('purchase_operations')
    op.drop_table('profiles')
    op.drop_table('prestige_upgrade_types')
    op.drop_table('player_level_progress')
    op.drop_index(op.f('ix_player_effects_account_id'), table_name='player_effects')
    op.drop_table('player_effects')
    op.drop_table('payment_providers')
    op.drop_table('payment_methods')
    op.drop_table('leaderboards')
    op.drop_table('inventory')
    op.drop_table('idempotency_records')
    op.drop_table('game_servers')
    op.drop_table('currencies')
    op.drop_table('content_releases')
    op.drop_table('consumed_messages')
    op.drop_table('case_types')
    op.drop_table('account_preferences')
