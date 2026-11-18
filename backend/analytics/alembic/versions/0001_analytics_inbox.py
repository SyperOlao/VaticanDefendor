"""Complete analytics schema for a fresh database."""
from alembic import op
import sqlalchemy as sa
from sqlalchemy.dialects import postgresql

revision = "0001_analytics_inbox"
down_revision = None
branch_labels = None
depends_on = None


def upgrade():
    op.create_table('analytics_inbox',
    sa.Column('event_name', sa.String(length=64), server_default='run.analytics', nullable=False),
    sa.Column('source_id', sa.Uuid(), nullable=False),
    sa.Column('account_id', sa.Uuid(), nullable=False),
    sa.Column('payload_hash', sa.LargeBinary(), nullable=False),
    sa.Column('payload_json', postgresql.JSONB(astext_type=sa.Text()), nullable=False),
    sa.Column('status', sa.String(length=16), server_default='PENDING', nullable=False),
    sa.Column('attempts', sa.Integer(), server_default='0', nullable=False),
    sa.Column('available_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.Column('lease_token', sa.Uuid(), nullable=True),
    sa.Column('last_error', sa.String(length=64), nullable=True),
    sa.Column('receipt_json', postgresql.JSONB(astext_type=sa.Text()), nullable=True),
    sa.Column('completed_at', sa.DateTime(timezone=True), nullable=True),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.CheckConstraint("(status = 'COMPLETED') = (completed_at IS NOT NULL AND receipt_json IS NOT NULL)", name=op.f('ck_analytics_inbox_completion')),
    sa.CheckConstraint("event_name IN ('run.analytics','level-gift.analytics','case.analytics','shop.purchase.analytics','wallet.transaction.analytics')", name=op.f('ck_analytics_inbox_event_type')),
    sa.CheckConstraint("status IN ('PENDING','COMPLETED','REVIEW') AND attempts >= 0", name=op.f('ck_analytics_inbox_state')),
    sa.CheckConstraint('octet_length(payload_hash) = 32', name=op.f('ck_analytics_inbox_hash_length')),
    sa.PrimaryKeyConstraint('event_name', 'source_id', name=op.f('analytics_inbox_pkey'))
    )
    op.create_index('ix_analytics_inbox_pending', 'analytics_inbox', ['available_at', 'created_at'], unique=False, postgresql_where=sa.text("status = 'PENDING'"))
    op.create_table('received_messages',
    sa.Column('message_id', sa.Uuid(), nullable=False),
    sa.Column('event_name', sa.String(length=64), server_default='run.analytics', nullable=False),
    sa.Column('source_id', sa.Uuid(), nullable=False),
    sa.Column('payload_hash', sa.LargeBinary(), nullable=False),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.CheckConstraint('octet_length(payload_hash) = 32', name=op.f('ck_received_messages_hash_length')),
    sa.ForeignKeyConstraint(['event_name', 'source_id'], ['analytics_inbox.event_name', 'analytics_inbox.source_id'], name=op.f('received_messages_event_name_fkey')),
    sa.PrimaryKeyConstraint('message_id', name=op.f('received_messages_pkey'))
    )


def downgrade():
    op.drop_table('received_messages')
    op.drop_index('ix_analytics_inbox_pending', table_name='analytics_inbox', postgresql_where=sa.text("status = 'PENDING'"))
    op.drop_table('analytics_inbox')
