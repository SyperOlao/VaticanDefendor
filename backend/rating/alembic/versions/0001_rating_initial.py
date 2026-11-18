"""Complete initial rating schema: scores, source receipts and durable RabbitMQ delivery."""
from uuid import NAMESPACE_URL, uuid5

from alembic import op
import sqlalchemy as sa
from sqlalchemy.dialects import postgresql

revision = '0001_rating_initial'
down_revision = None
branch_labels = None
depends_on = None


def created_at():
    return sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.func.now(), nullable=False)


def upgrade():
    op.create_table('leaderboards',
        sa.Column('id', sa.Uuid(), nullable=False),
        sa.Column('code', sa.String(64), nullable=False),
        sa.Column('score_source', sa.String(64), nullable=False),
        sa.PrimaryKeyConstraint('id', name=op.f('leaderboards_pkey')),
        sa.UniqueConstraint('code', name=op.f('leaderboards_code_key')),
    )
    op.create_table('leaderboard_scores',
        sa.Column('board_id', sa.Uuid(), nullable=False),
        sa.Column('account_id', sa.Uuid(), nullable=False),
        sa.Column('score', sa.BigInteger(), nullable=False),
        sa.Column('updated_at', sa.DateTime(timezone=True), server_default=sa.func.now(), nullable=False),
        sa.PrimaryKeyConstraint('board_id', 'account_id', name=op.f('leaderboard_scores_pkey')),
        sa.ForeignKeyConstraint(['board_id'], ['leaderboards.id'], name=op.f('leaderboard_scores_board_id_fkey')),
        sa.CheckConstraint('score >= 0', name=op.f('ck_leaderboard_scores_score_nonnegative')),
    )
    op.create_index('ix_leaderboard_scores_ranking', 'leaderboard_scores',
                    ['board_id', sa.literal_column('score DESC'), 'account_id'])
    op.create_table('run_score_receipts',
        sa.Column('run_id', sa.Uuid(), nullable=False),
        sa.Column('account_id', sa.Uuid(), nullable=False),
        sa.Column('board_id', sa.Uuid(), nullable=False),
        sa.Column('ruleset_id', sa.String(128), nullable=False),
        sa.Column('content_release_id', sa.String(128), nullable=False),
        sa.Column('points', sa.BigInteger(), nullable=False),
        sa.Column('payload_hash', sa.LargeBinary(), nullable=False),
        sa.Column('receipt_json', postgresql.JSONB(), nullable=False), created_at(),
        sa.PrimaryKeyConstraint('run_id', name=op.f('run_score_receipts_pkey')),
        sa.ForeignKeyConstraint(['board_id'], ['leaderboards.id'], name=op.f('run_score_receipts_board_id_fkey')),
        sa.CheckConstraint('points >= 0', name=op.f('ck_run_score_receipts_points_nonnegative')),
        sa.CheckConstraint('octet_length(payload_hash) = 32', name=op.f('ck_run_score_receipts_hash_length')),
    )
    op.create_table('consumed_messages',
        sa.Column('consumer', sa.String(64), nullable=False),
        sa.Column('message_id', sa.Uuid(), nullable=False),
        sa.Column('payload_hash', sa.LargeBinary(), nullable=True), created_at(),
        sa.PrimaryKeyConstraint('consumer', 'message_id', name=op.f('consumed_messages_pkey')),
    )
    op.create_table('idempotency_records',
        sa.Column('account_id', sa.Uuid(), nullable=False),
        sa.Column('operation', sa.String(64), nullable=False),
        sa.Column('request_key', sa.String(128), nullable=False),
        sa.Column('payload_hash', sa.LargeBinary(), nullable=False),
        sa.Column('status', sa.String(16), server_default='PENDING', nullable=False),
        sa.Column('result_reference', sa.String(255), nullable=True),
        sa.Column('result_json', postgresql.JSONB(), nullable=True),
        sa.Column('completed_at', sa.DateTime(timezone=True), nullable=True), created_at(),
        sa.PrimaryKeyConstraint('account_id', 'operation', 'request_key', name=op.f('idempotency_records_pkey')),
        sa.CheckConstraint("status IN ('PENDING','UNKNOWN','COMPLETED','REJECTED')", name=op.f('ck_idempotency_records_status')),
        sa.CheckConstraint('octet_length(payload_hash) = 32', name=op.f('ck_idempotency_records_hash_length')),
        sa.CheckConstraint("(status IN ('COMPLETED','REJECTED')) = (completed_at IS NOT NULL)",
                           name=op.f('ck_idempotency_records_completion_time')),
    )
    op.create_table('outbox_messages',
        sa.Column('id', sa.Uuid(), nullable=False),
        sa.Column('destination', sa.String(64), nullable=False),
        sa.Column('event_name', sa.String(128), nullable=False),
        sa.Column('schema_version', sa.Integer(), nullable=False),
        sa.Column('aggregate_id', sa.Uuid(), nullable=False),
        sa.Column('payload', postgresql.JSONB(), nullable=False),
        sa.Column('available_at', sa.DateTime(timezone=True), server_default=sa.func.now(), nullable=False),
        sa.Column('published_at', sa.DateTime(timezone=True), nullable=True),
        sa.Column('attempts', sa.Integer(), server_default='0', nullable=False),
        sa.Column('last_error', sa.Text(), nullable=True),
        sa.Column('lease_token', sa.Uuid(), nullable=True),
        sa.Column('failed_at', sa.DateTime(timezone=True), nullable=True), created_at(),
        sa.PrimaryKeyConstraint('id', name=op.f('outbox_messages_pkey')),
        sa.CheckConstraint('schema_version > 0 AND attempts >= 0', name=op.f('ck_outbox_messages_counters')),
    )
    op.create_index('ix_outbox_pending', 'outbox_messages', ['available_at', 'created_at'],
                    postgresql_where=sa.text('published_at IS NULL'))
    op.create_table('broker_inbox',
        sa.Column('message_id', sa.Uuid(), nullable=False),
        sa.Column('envelope', postgresql.JSON(), nullable=False),
        sa.Column('payload_hash', sa.LargeBinary(), nullable=False),
        sa.Column('status', sa.String(16), server_default='PENDING', nullable=False),
        sa.Column('attempts', sa.Integer(), server_default='0', nullable=False),
        sa.Column('generation', sa.Integer(), server_default='1', nullable=False),
        sa.Column('available_at', sa.DateTime(timezone=True), server_default=sa.func.now(), nullable=False),
        sa.Column('lease_token', sa.Uuid(), nullable=True),
        sa.Column('last_error', sa.String(64), nullable=True),
        sa.Column('result_json', postgresql.JSONB(), nullable=True),
        sa.Column('finished_at', sa.DateTime(timezone=True), nullable=True), created_at(),
        sa.PrimaryKeyConstraint('message_id', name=op.f('broker_inbox_pkey')),
        sa.CheckConstraint("status IN ('PENDING','APPLIED','REJECTED','REVIEW')", name=op.f('ck_broker_inbox_status')),
        sa.CheckConstraint('attempts >= 0 AND generation > 0', name=op.f('ck_broker_inbox_counters')),
        sa.CheckConstraint('octet_length(payload_hash) = 32', name=op.f('ck_broker_inbox_hash_length')),
        sa.CheckConstraint("(status = 'PENDING') = (finished_at IS NULL)", name=op.f('ck_broker_inbox_completion_time')),
    )
    op.create_index('ix_broker_inbox_pending', 'broker_inbox', ['available_at', 'created_at'],
                    postgresql_where=sa.text("status = 'PENDING'"))
    op.create_table('broker_rejections',
        sa.Column('id', sa.Uuid(), nullable=False),
        sa.Column('message_id', sa.Uuid(), nullable=True),
        sa.Column('body_hash', sa.LargeBinary(), nullable=False),
        sa.Column('body_prefix', sa.LargeBinary(), nullable=False),
        sa.Column('body_size', sa.BigInteger(), nullable=False),
        sa.Column('code', sa.String(64), nullable=False), created_at(),
        sa.PrimaryKeyConstraint('id', name=op.f('broker_rejections_pkey')),
        sa.CheckConstraint('octet_length(body_hash) = 32', name=op.f('ck_broker_rejections_hash_length')),
        sa.CheckConstraint('body_size >= 0 AND octet_length(body_prefix) <= 4096', name=op.f('ck_broker_rejections_body_size')),
    )
    boards = sa.table('leaderboards', sa.column('id', sa.Uuid()), sa.column('code', sa.String(64)),
                      sa.column('score_source', sa.String(64)))
    op.bulk_insert(boards, [{'id': uuid5(NAMESPACE_URL, 'vaticandefendor:rating:run-xp'),
                             'code': 'run-xp', 'score_source': 'RUN_XP'}])


def downgrade():
    for table in ('broker_rejections', 'broker_inbox', 'outbox_messages', 'idempotency_records',
                  'consumed_messages', 'run_score_receipts', 'leaderboard_scores', 'leaderboards'):
        op.drop_table(table)
