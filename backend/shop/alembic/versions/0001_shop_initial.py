"""Complete shop schema for a fresh database."""
from alembic import op
import sqlalchemy as sa
from sqlalchemy.dialects import postgresql

revision = "0001_shop_initial"
down_revision = None
branch_labels = None
depends_on = None


def upgrade():
    op.create_table('consumed_messages',
    sa.Column('consumer', sa.String(length=64), nullable=False),
    sa.Column('message_id', sa.Uuid(), nullable=False),
    sa.Column('payload_hash', sa.LargeBinary(), nullable=True),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.PrimaryKeyConstraint('consumer', 'message_id', name=op.f('consumed_messages_pkey'))
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
    op.create_table('purchase_operations',
    sa.Column('account_id', sa.Uuid(), nullable=False),
    sa.Column('operation_id', sa.Uuid(), nullable=False),
    sa.Column('offer_id', sa.String(length=64), nullable=False),
    sa.Column('catalog_version', sa.String(length=64), nullable=False),
    sa.Column('receipt_json', postgresql.JSONB(astext_type=sa.Text()), nullable=False),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.PrimaryKeyConstraint('account_id', 'operation_id', name=op.f('purchase_operations_pkey'))
    )
    op.create_index('purchase_operations_admin_history_idx', 'purchase_operations', ['created_at', 'account_id', 'operation_id'], unique=False)
    op.create_index('purchase_operations_admin_status_idx', 'purchase_operations', [sa.literal_column("(receipt_json->>'status')"), 'created_at', 'account_id', 'operation_id'], unique=False)
    op.create_index('purchase_operations_completed_offer_idx', 'purchase_operations', ['account_id', 'offer_id'], unique=False, postgresql_where=sa.text("receipt_json->>'status'='Completed'"))
    op.create_index('purchase_operations_history_idx', 'purchase_operations', ['account_id', 'created_at', 'operation_id'], unique=False)
    op.create_table('shop_catalog_state',
    sa.Column('id', sa.Integer(), nullable=False),
    sa.Column('revision', sa.BigInteger(), server_default='1', nullable=False),
    sa.CheckConstraint('id = 1 AND revision > 0', name=op.f('ck_shop_catalog_state_singleton_revision')),
    sa.PrimaryKeyConstraint('id', name=op.f('shop_catalog_state_pkey'))
    )
    op.create_table('shop_offers',
    sa.Column('id', sa.String(length=64), nullable=False),
    sa.Column('configuration_id', sa.String(length=80), nullable=False),
    sa.Column('title', sa.String(length=80), nullable=False),
    sa.Column('image_key', sa.String(length=96), nullable=True),
    sa.Column('price', sa.BigInteger(), nullable=False),
    sa.Column('purchase_limit', sa.BigInteger(), server_default='0', nullable=False),
    sa.Column('available', sa.Boolean(), server_default='true', nullable=False),
    sa.Column('unavailable_reason', sa.String(length=64), server_default='', nullable=False),
    sa.Column('revision', sa.Integer(), server_default='1', nullable=False),
    sa.Column('updated_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.Column('deleted_at', sa.DateTime(timezone=True), nullable=True),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.CheckConstraint("NOT available OR (unavailable_reason = '' AND deleted_at IS NULL)", name=op.f('ck_shop_offers_availability')),
    sa.CheckConstraint("id ~ '^[a-z0-9_.-]{1,64}$' AND configuration_id ~ '^[A-Za-z0-9_.-]{1,80}$'", name=op.f('ck_shop_offers_identifiers')),
    sa.CheckConstraint("image_key IS NULL OR image_key ~ '^shop/items/[a-f0-9]{64}\\.(png|jpg)$'", name=op.f('ck_shop_offers_image_key')),
    sa.CheckConstraint("unavailable_reason = '' OR unavailable_reason ~ '^[A-Za-z][A-Za-z0-9]{0,63}$'", name=op.f('ck_shop_offers_reason')),
    sa.CheckConstraint('length(btrim(title)) > 0', name=op.f('ck_shop_offers_title')),
    sa.CheckConstraint('price >= 0 AND purchase_limit >= 0 AND revision > 0', name=op.f('ck_shop_offers_values')),
    sa.PrimaryKeyConstraint('id', name=op.f('shop_offers_pkey'))
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
    sa.PrimaryKeyConstraint('id', name=op.f('shop_products_pkey')),
    sa.UniqueConstraint('internal_sku', 'version', name=op.f('shop_products_internal_sku_version_key'))
    )
    op.create_table('storefronts',
    sa.Column('id', sa.Integer(), nullable=False),
    sa.Column('code', sa.String(length=32), nullable=False),
    sa.Column('platform', sa.String(length=32), nullable=False),
    sa.Column('enabled', sa.Boolean(), server_default='false', nullable=False),
    sa.PrimaryKeyConstraint('id', name=op.f('storefronts_pkey')),
    sa.UniqueConstraint('code', name=op.f('storefronts_code_key'))
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
    op.create_table('purchase_events',
    sa.Column('account_id', sa.Uuid(), nullable=False),
    sa.Column('operation_id', sa.Uuid(), nullable=False),
    sa.Column('sequence', sa.BigInteger(), nullable=False),
    sa.Column('kind', sa.String(length=32), nullable=False),
    sa.Column('from_state', sa.String(length=32), nullable=True),
    sa.Column('state', sa.String(length=32), nullable=False),
    sa.Column('attempts', sa.Integer(), nullable=True),
    sa.Column('error_code', sa.String(length=64), nullable=True),
    sa.Column('actor_id', sa.Uuid(), nullable=True),
    sa.Column('request_key', sa.Uuid(), nullable=True),
    sa.Column('reason', sa.String(length=500), nullable=True),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.CheckConstraint("kind != 'ADMIN_RETRY' OR (actor_id IS NOT NULL AND request_key IS NOT NULL AND reason IS NOT NULL AND length(btrim(reason)) > 0)", name=op.f('ck_purchase_events_admin_actor')),
    sa.CheckConstraint("kind IN ('CREATED','CLAIMED','CHARGED','REFUND_PENDING','COMPLETED','REJECTED','RETRY_SCHEDULED','REVIEW','ADMIN_RETRY','CLI_RETRY','LEGACY_SNAPSHOT')", name=op.f('ck_purchase_events_kind')),
    sa.CheckConstraint("state IN ('PENDING','CHARGED','REFUND_PENDING','COMPLETED','REJECTED','REVIEW')", name=op.f('ck_purchase_events_state')),
    sa.CheckConstraint('sequence > 0 AND (attempts IS NULL OR attempts >= 0)', name=op.f('ck_purchase_events_counters')),
    sa.ForeignKeyConstraint(['account_id', 'operation_id'], ['purchase_operations.account_id', 'purchase_operations.operation_id'], name=op.f('purchase_events_account_id_fkey')),
    sa.PrimaryKeyConstraint('account_id', 'operation_id', 'sequence', name=op.f('purchase_events_pkey'))
    )
    op.create_table('purchase_jobs',
    sa.Column('account_id', sa.Uuid(), nullable=False),
    sa.Column('operation_id', sa.Uuid(), nullable=False),
    sa.Column('state', sa.String(length=32), server_default='PENDING', nullable=False),
    sa.Column('lease_token', sa.Uuid(), nullable=True),
    sa.Column('available_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.Column('attempts', sa.Integer(), server_default='0', nullable=False),
    sa.Column('version', sa.BigInteger(), server_default='1', nullable=False),
    sa.Column('last_error_code', sa.String(length=64), nullable=True),
    sa.CheckConstraint("state IN ('PENDING','CHARGED','REFUND_PENDING','COMPLETED','REJECTED','REVIEW')", name=op.f('ck_purchase_jobs_state')),
    sa.CheckConstraint('attempts >= 0', name=op.f('ck_purchase_jobs_attempts_nonnegative')),
    sa.CheckConstraint('version > 0', name=op.f('ck_purchase_jobs_version_positive')),
    sa.ForeignKeyConstraint(['account_id', 'operation_id'], ['purchase_operations.account_id', 'purchase_operations.operation_id'], name=op.f('purchase_jobs_account_id_fkey')),
    sa.PrimaryKeyConstraint('account_id', 'operation_id', name=op.f('purchase_jobs_pkey'))
    )
    op.create_index('purchase_jobs_available_idx', 'purchase_jobs', ['available_at'], unique=False, postgresql_where=sa.text("state IN ('PENDING','CHARGED','REFUND_PENDING')"))
    op.create_table('shop_catalog_audit',
    sa.Column('actor_account_id', sa.Uuid(), nullable=False),
    sa.Column('offer_id', sa.String(length=64), nullable=False),
    sa.Column('action', sa.String(length=32), nullable=False),
    sa.Column('before_json', postgresql.JSONB(astext_type=sa.Text()), nullable=True),
    sa.Column('after_json', postgresql.JSONB(astext_type=sa.Text()), nullable=False),
    sa.Column('id', sa.Uuid(), nullable=False),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.CheckConstraint("action IN ('shop.offer.create','shop.offer.update','shop.offer.delete')", name=op.f('ck_shop_catalog_audit_action')),
    sa.ForeignKeyConstraint(['offer_id'], ['shop_offers.id'], name=op.f('shop_catalog_audit_offer_id_fkey')),
    sa.PrimaryKeyConstraint('id', name=op.f('shop_catalog_audit_pkey'))
    )
    op.create_index('shop_catalog_audit_offer_idx', 'shop_catalog_audit', ['offer_id', 'created_at'], unique=False)
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
    op.create_table('mock_products',
    sa.Column('product_id', sa.Uuid(), nullable=False),
    sa.Column('mapping_id', sa.Uuid(), nullable=False),
    sa.Column('configuration_hash', sa.LargeBinary(), nullable=False),
    sa.Column('snapshot', postgresql.JSONB(astext_type=sa.Text()), nullable=False),
    sa.CheckConstraint('octet_length(configuration_hash) = 32', name=op.f('ck_mock_products_hash_length')),
    sa.ForeignKeyConstraint(['mapping_id'], ['store_product_mappings.id'], name=op.f('mock_products_mapping_id_fkey')),
    sa.ForeignKeyConstraint(['product_id'], ['shop_products.id'], name=op.f('mock_products_product_id_fkey')),
    sa.PrimaryKeyConstraint('product_id', name=op.f('mock_products_pkey')),
    sa.UniqueConstraint('mapping_id', name=op.f('mock_products_mapping_id_key'))
    )
    op.create_table('mock_provider_payments',
    sa.Column('order_id', sa.Uuid(), nullable=False),
    sa.Column('truth_json', postgresql.JSONB(astext_type=sa.Text()), nullable=False),
    sa.Column('unavailable', sa.Boolean(), server_default='false', nullable=False),
    sa.Column('updated_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.ForeignKeyConstraint(['order_id'], ['payment_orders.id'], name=op.f('mock_provider_payments_order_id_fkey')),
    sa.PrimaryKeyConstraint('order_id', name=op.f('mock_provider_payments_pkey'))
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
    op.create_table('mock_fulfillment_jobs',
    sa.Column('order_id', sa.Uuid(), nullable=False),
    sa.Column('line_index', sa.Integer(), nullable=False),
    sa.Column('unit_index', sa.Integer(), nullable=False),
    sa.Column('transaction_id', sa.Uuid(), nullable=False),
    sa.Column('configuration_id', sa.String(length=80), nullable=False),
    sa.Column('state', sa.String(length=32), server_default='PENDING', nullable=False),
    sa.Column('lease_token', sa.Uuid(), nullable=True),
    sa.Column('available_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.Column('attempts', sa.Integer(), server_default='0', nullable=False),
    sa.Column('last_error', sa.String(length=64), nullable=True),
    sa.Column('receipt_json', postgresql.JSONB(astext_type=sa.Text()), nullable=True),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.CheckConstraint("state IN ('PENDING','COMPLETED','REVIEW','CANCELLED')", name=op.f('ck_mock_fulfillment_jobs_state')),
    sa.CheckConstraint('line_index >= 0 AND unit_index >= 0 AND attempts >= 0', name=op.f('ck_mock_fulfillment_jobs_counters')),
    sa.ForeignKeyConstraint(['order_id'], ['payment_orders.id'], name=op.f('mock_fulfillment_jobs_order_id_fkey')),
    sa.ForeignKeyConstraint(['transaction_id'], ['purchase_transactions.id'], name=op.f('mock_fulfillment_jobs_transaction_id_fkey')),
    sa.PrimaryKeyConstraint('order_id', 'line_index', 'unit_index', name=op.f('mock_fulfillment_jobs_pkey'))
    )
    op.create_index('mock_fulfillment_available_idx', 'mock_fulfillment_jobs', ['state', 'available_at'], unique=False)
    op.create_table('mock_order_states',
    sa.Column('order_id', sa.Uuid(), nullable=False),
    sa.Column('snapshot', postgresql.JSONB(astext_type=sa.Text()), nullable=False),
    sa.Column('payload_hash', sa.LargeBinary(), nullable=False),
    sa.Column('transaction_id', sa.Uuid(), nullable=True),
    sa.Column('fulfillment_status', sa.String(length=32), server_default='NOT_READY', nullable=False),
    sa.Column('version', sa.BigInteger(), server_default='1', nullable=False),
    sa.Column('last_provider_version', sa.Integer(), server_default='0', nullable=False),
    sa.Column('last_provider_status', sa.String(length=16), server_default='PENDING', nullable=False),
    sa.Column('verify_enabled', sa.Boolean(), server_default='true', nullable=False),
    sa.Column('verify_pending', sa.Boolean(), server_default='true', nullable=False),
    sa.Column('available_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.Column('lease_token', sa.Uuid(), nullable=True),
    sa.Column('attempts', sa.Integer(), server_default='0', nullable=False),
    sa.Column('last_error', sa.String(length=64), nullable=True),
    sa.Column('review_code', sa.String(length=64), nullable=True),
    sa.CheckConstraint("fulfillment_status IN ('NOT_READY','PENDING','PARTIAL','COMPLETED','CANCELLED','REVIEW')", name=op.f('ck_mock_order_states_fulfillment')),
    sa.CheckConstraint('octet_length(payload_hash) = 32', name=op.f('ck_mock_order_states_hash_length')),
    sa.CheckConstraint('version > 0 AND last_provider_version >= 0 AND attempts >= 0', name=op.f('ck_mock_order_states_counters')),
    sa.ForeignKeyConstraint(['order_id'], ['payment_orders.id'], name=op.f('mock_order_states_order_id_fkey')),
    sa.ForeignKeyConstraint(['transaction_id'], ['purchase_transactions.id'], name=op.f('mock_order_states_transaction_id_fkey')),
    sa.PrimaryKeyConstraint('order_id', name=op.f('mock_order_states_pkey'))
    )
    op.create_index('mock_orders_verify_idx', 'mock_order_states', ['verify_enabled', 'available_at'], unique=False)
    op.create_table('outbox_messages',
    sa.Column('destination', sa.String(length=64), nullable=False),
    sa.Column('event_name', sa.String(length=128), nullable=False),
    sa.Column('schema_version', sa.Integer(), nullable=False),
    sa.Column('aggregate_id', sa.Uuid(), nullable=False),
    sa.Column('payment_transaction_id', sa.Uuid(), nullable=True),
    sa.Column('payload', postgresql.JSONB(astext_type=sa.Text()), nullable=False),
    sa.Column('available_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.Column('published_at', sa.DateTime(timezone=True), nullable=True),
    sa.Column('lease_token', sa.Uuid(), nullable=True),
    sa.Column('failed_at', sa.DateTime(timezone=True), nullable=True),
    sa.Column('receipt_json', postgresql.JSONB(astext_type=sa.Text()), nullable=True),
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
    op.execute(sa.text("INSERT INTO shop_catalog_state (id,revision) VALUES (1,1)"))



def downgrade():
    op.drop_index('ix_outbox_pending', table_name='outbox_messages', postgresql_where=sa.text('published_at IS NULL'))
    op.drop_table('outbox_messages')
    op.drop_index('mock_orders_verify_idx', table_name='mock_order_states')
    op.drop_table('mock_order_states')
    op.drop_index('mock_fulfillment_available_idx', table_name='mock_fulfillment_jobs')
    op.drop_table('mock_fulfillment_jobs')
    op.drop_table('entitlement_grants')
    op.drop_index(op.f('ix_purchase_transactions_order_id'), table_name='purchase_transactions')
    op.drop_table('purchase_transactions')
    op.drop_index(op.f('ix_payment_attempts_order_id'), table_name='payment_attempts')
    op.drop_table('payment_attempts')
    op.drop_table('mock_provider_payments')
    op.drop_table('mock_products')
    op.drop_table('store_product_mappings')
    op.drop_index('shop_catalog_audit_offer_idx', table_name='shop_catalog_audit')
    op.drop_table('shop_catalog_audit')
    op.drop_index('purchase_jobs_available_idx', table_name='purchase_jobs', postgresql_where=sa.text("state IN ('PENDING','CHARGED','REFUND_PENDING')"))
    op.drop_table('purchase_jobs')
    op.drop_table('purchase_events')
    op.drop_table('provider_notification_inbox')
    op.drop_table('payment_routing_rules')
    op.drop_index(op.f('ix_payment_orders_account_id'), table_name='payment_orders')
    op.drop_table('payment_orders')
    op.drop_table('storefronts')
    op.drop_table('shop_products')
    op.drop_table('shop_offers')
    op.drop_table('shop_catalog_state')
    op.drop_index('purchase_operations_history_idx', table_name='purchase_operations')
    op.drop_index('purchase_operations_completed_offer_idx', table_name='purchase_operations', postgresql_where=sa.text("receipt_json->>'status'='Completed'"))
    op.drop_index('purchase_operations_admin_status_idx', table_name='purchase_operations')
    op.drop_index('purchase_operations_admin_history_idx', table_name='purchase_operations')
    op.drop_table('purchase_operations')
    op.drop_table('payment_providers')
    op.drop_table('payment_methods')
    op.drop_table('idempotency_records')
    op.drop_table('consumed_messages')
