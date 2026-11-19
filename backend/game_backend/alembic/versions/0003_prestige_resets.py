"""Track confirmed prestige resets and preserve the original purchase receipts."""
from alembic import op
import sqlalchemy as sa


revision = '0003_prestige_resets'
down_revision = '0002_profile_name_lookup'
branch_labels = None
depends_on = None


def upgrade():
    op.create_table(
        'prestige_resets',
        sa.Column('id', sa.Uuid(), primary_key=True),
        sa.Column('account_id', sa.Uuid(), nullable=False),
        sa.Column('request_key', sa.Uuid(), nullable=False),
        sa.Column('revision', sa.String(64), nullable=False),
        sa.Column('refund_message_id', sa.Uuid(), sa.ForeignKey('outbox_messages.id'), nullable=False, unique=True),
        sa.Column('status', sa.String(16), nullable=False),
        sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.func.now(), nullable=False),
        sa.UniqueConstraint('account_id', 'request_key'),
        sa.CheckConstraint("status IN ('PENDING','COMPLETED','REJECTED')", name='status'),
    )
    op.create_index('prestige_pending_reset_account_idx', 'prestige_resets', ['account_id'],
                    unique=True, postgresql_where=sa.text("status = 'PENDING'"))
    op.add_column('prestige_purchases', sa.Column('reset_id', sa.Uuid(), nullable=True))
    op.create_foreign_key('prestige_purchases_reset_id_fkey', 'prestige_purchases', 'prestige_resets',
                          ['reset_id'], ['id'])


def downgrade():
    # Older workers cannot settle outstanding refunds or distinguish previously refunded purchases.
    if op.get_bind().scalar(sa.text('SELECT EXISTS (SELECT 1 FROM prestige_resets)')):
        raise RuntimeError('Cannot downgrade a database with prestige reset history.')
    op.drop_constraint('prestige_purchases_reset_id_fkey', 'prestige_purchases', type_='foreignkey')
    op.drop_column('prestige_purchases', 'reset_id')
    op.drop_table('prestige_resets')
