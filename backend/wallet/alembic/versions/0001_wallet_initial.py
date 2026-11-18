"""Complete wallet schema for a fresh database."""
from alembic import op
import sqlalchemy as sa
from sqlalchemy.dialects import postgresql

revision = "0001_wallet_initial"
down_revision = None
branch_labels = None
depends_on = None


def upgrade():
    op.create_table('broker_inbox',
    sa.Column('message_id', sa.Uuid(), nullable=False),
    sa.Column('envelope', sa.JSON(), nullable=False),
    sa.Column('payload_hash', sa.LargeBinary(), nullable=False),
    sa.Column('status', sa.String(length=16), server_default='PENDING', nullable=False),
    sa.Column('attempts', sa.Integer(), server_default='0', nullable=False),
    sa.Column('generation', sa.Integer(), server_default='1', nullable=False),
    sa.Column('available_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.Column('lease_token', sa.Uuid(), nullable=True),
    sa.Column('last_error', sa.String(length=64), nullable=True),
    sa.Column('result_json', postgresql.JSONB(astext_type=sa.Text()), nullable=True),
    sa.Column('finished_at', sa.DateTime(timezone=True), nullable=True),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.CheckConstraint("(status = 'PENDING') = (finished_at IS NULL)", name=op.f('ck_broker_inbox_completion_time')),
    sa.CheckConstraint("status IN ('PENDING','APPLIED','REJECTED','REVIEW')", name=op.f('ck_broker_inbox_status')),
    sa.CheckConstraint('attempts >= 0 AND generation > 0', name=op.f('ck_broker_inbox_counters')),
    sa.CheckConstraint('octet_length(payload_hash) = 32', name=op.f('ck_broker_inbox_hash_length')),
    sa.PrimaryKeyConstraint('message_id', name=op.f('broker_inbox_pkey'))
    )
    op.create_index('ix_broker_inbox_pending', 'broker_inbox', ['available_at', 'created_at'], unique=False, postgresql_where=sa.text("status = 'PENDING'"))
    op.create_table('broker_rejections',
    sa.Column('id', sa.Uuid(), nullable=False),
    sa.Column('message_id', sa.Uuid(), nullable=True),
    sa.Column('body_hash', sa.LargeBinary(), nullable=False),
    sa.Column('body_prefix', sa.LargeBinary(), nullable=False),
    sa.Column('body_size', sa.BigInteger(), nullable=False),
    sa.Column('code', sa.String(length=64), nullable=False),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.CheckConstraint('body_size >= 0 AND octet_length(body_prefix) <= 4096', name=op.f('ck_broker_rejections_body_size')),
    sa.CheckConstraint('octet_length(body_hash) = 32', name=op.f('ck_broker_rejections_hash_length')),
    sa.PrimaryKeyConstraint('id', name=op.f('broker_rejections_pkey'))
    )
    op.create_table('case_operation_receipts',
    sa.Column('opening_id', sa.Uuid(), nullable=False),
    sa.Column('item_index', sa.Integer(), nullable=False),
    sa.Column('account_id', sa.Uuid(), nullable=False),
    sa.Column('payload_hash', sa.LargeBinary(), nullable=False),
    sa.Column('receipt_json', postgresql.JSONB(astext_type=sa.Text()), nullable=False),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.CheckConstraint('item_index >= -1', name=op.f('ck_case_operation_receipts_index_range')),
    sa.CheckConstraint('octet_length(payload_hash) = 32', name=op.f('ck_case_operation_receipts_hash_length')),
    sa.PrimaryKeyConstraint('opening_id', 'item_index', name=op.f('case_operation_receipts_pkey'))
    )
    op.create_table('consumed_messages',
    sa.Column('consumer', sa.String(length=64), nullable=False),
    sa.Column('message_id', sa.Uuid(), nullable=False),
    sa.Column('payload_hash', sa.LargeBinary(), nullable=True),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.PrimaryKeyConstraint('consumer', 'message_id', name=op.f('consumed_messages_pkey'))
    )
    op.create_table('currencies',
    sa.Column('code', sa.String(length=32), nullable=False),
    sa.PrimaryKeyConstraint('code', name=op.f('currencies_pkey'))
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
    op.create_table('level_reward_receipts',
    sa.Column('claim_id', sa.Uuid(), nullable=False),
    sa.Column('item_index', sa.Integer(), nullable=False),
    sa.Column('account_id', sa.Uuid(), nullable=False),
    sa.Column('payload_hash', sa.LargeBinary(), nullable=False),
    sa.Column('receipt_json', postgresql.JSONB(astext_type=sa.Text()), nullable=False),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.CheckConstraint('item_index >= 0', name=op.f('ck_level_reward_receipts_index_nonnegative')),
    sa.CheckConstraint('octet_length(payload_hash) = 32', name=op.f('ck_level_reward_receipts_hash_length')),
    sa.PrimaryKeyConstraint('claim_id', 'item_index', name=op.f('level_reward_receipts_pkey'))
    )
    op.create_table('outbox_messages',
    sa.Column('destination', sa.String(length=64), nullable=False),
    sa.Column('event_name', sa.String(length=128), nullable=False),
    sa.Column('schema_version', sa.Integer(), nullable=False),
    sa.Column('aggregate_id', sa.Uuid(), nullable=False),
    sa.Column('payload', postgresql.JSONB(astext_type=sa.Text()), nullable=False),
    sa.Column('available_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.Column('published_at', sa.DateTime(timezone=True), nullable=True),
    sa.Column('attempts', sa.Integer(), server_default='0', nullable=False),
    sa.Column('last_error', sa.Text(), nullable=True),
    sa.Column('lease_token', sa.Uuid(), nullable=True),
    sa.Column('failed_at', sa.DateTime(timezone=True), nullable=True),
    sa.Column('receipt_json', postgresql.JSONB(astext_type=sa.Text()), nullable=True),
    sa.Column('id', sa.Uuid(), nullable=False),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.CheckConstraint('schema_version > 0 AND attempts >= 0', name=op.f('ck_outbox_messages_counters')),
    sa.PrimaryKeyConstraint('id', name=op.f('outbox_messages_pkey'))
    )
    op.create_index('ix_outbox_pending', 'outbox_messages', ['available_at', 'created_at'], unique=False, postgresql_where=sa.text('published_at IS NULL'))
    op.create_table('prestige_payment_receipts',
    sa.Column('purchase_id', sa.Uuid(), nullable=False),
    sa.Column('account_id', sa.Uuid(), nullable=False),
    sa.Column('payload_hash', sa.LargeBinary(), nullable=False),
    sa.Column('receipt_json', postgresql.JSONB(astext_type=sa.Text()), nullable=False),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.CheckConstraint('octet_length(payload_hash) = 32', name=op.f('ck_prestige_payment_receipts_hash_length')),
    sa.PrimaryKeyConstraint('purchase_id', name=op.f('prestige_payment_receipts_pkey'))
    )
    op.create_table('run_reward_receipts',
    sa.Column('run_id', sa.Uuid(), nullable=False),
    sa.Column('account_id', sa.Uuid(), nullable=False),
    sa.Column('amount', sa.BigInteger(), nullable=False),
    sa.Column('receipt_json', postgresql.JSONB(astext_type=sa.Text()), nullable=False),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.CheckConstraint('amount > 0', name=op.f('ck_run_reward_receipts_amount_positive')),
    sa.PrimaryKeyConstraint('run_id', name=op.f('run_reward_receipts_pkey'))
    )
    op.create_table('currency_balances',
    sa.Column('account_id', sa.Uuid(), nullable=False),
    sa.Column('currency_code', sa.String(length=32), nullable=False),
    sa.Column('balance', sa.BigInteger(), server_default='0', nullable=False),
    sa.Column('revision', sa.BigInteger(), server_default='1', nullable=False),
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
    op.create_index('ix_economy_history', 'economy_transactions', ['account_id', 'created_at', 'id'], unique=False)
    op.create_index(op.f('ix_economy_transactions_account_id'), 'economy_transactions', ['account_id'], unique=False)
    op.create_index('uq_economy_run_source', 'economy_transactions', ['source_id'], unique=True, postgresql_where=sa.text("source_type = 'RUN_REWARD'"))


def downgrade():
    op.drop_index('uq_economy_run_source', table_name='economy_transactions', postgresql_where=sa.text("source_type = 'RUN_REWARD'"))
    op.drop_index(op.f('ix_economy_transactions_account_id'), table_name='economy_transactions')
    op.drop_index('ix_economy_history', table_name='economy_transactions')
    op.drop_table('economy_transactions')
    op.drop_table('currency_balances')
    op.drop_table('run_reward_receipts')
    op.drop_table('prestige_payment_receipts')
    op.drop_index('ix_outbox_pending', table_name='outbox_messages', postgresql_where=sa.text('published_at IS NULL'))
    op.drop_table('outbox_messages')
    op.drop_table('level_reward_receipts')
    op.drop_table('idempotency_records')
    op.drop_table('currencies')
    op.drop_table('consumed_messages')
    op.drop_table('case_operation_receipts')
    op.drop_table('broker_rejections')
    op.drop_index('ix_broker_inbox_pending', table_name='broker_inbox', postgresql_where=sa.text("status = 'PENDING'"))
    op.drop_table('broker_inbox')
