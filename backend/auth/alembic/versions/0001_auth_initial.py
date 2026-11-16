"""Initial auth-owned PostgreSQL schema."""
from alembic import op
import sqlalchemy as sa
from sqlalchemy.dialects import postgresql

revision = "0001_auth_initial"
down_revision = None
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.create_table('accounts',
    sa.Column('account_status', sa.String(length=32), server_default='ACTIVE', nullable=False),
    sa.Column('id', sa.Uuid(), nullable=False),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.PrimaryKeyConstraint('id', name=op.f('accounts_pkey'))
    )
    op.create_table('auth_attempts',
    sa.Column('key_hash', sa.LargeBinary(), nullable=False),
    sa.Column('window_start', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.Column('attempts', sa.Integer(), nullable=False),
    sa.PrimaryKeyConstraint('key_hash', name=op.f('auth_attempts_pkey'))
    )
    op.create_table('permissions',
    sa.Column('id', sa.Integer(), nullable=False),
    sa.Column('code', sa.String(length=128), nullable=False),
    sa.Column('description', sa.Text(), nullable=False),
    sa.PrimaryKeyConstraint('id', name=op.f('permissions_pkey')),
    sa.UniqueConstraint('code', name=op.f('permissions_code_key'))
    )
    op.create_table('roles',
    sa.Column('id', sa.Integer(), nullable=False),
    sa.Column('code', sa.String(length=64), nullable=False),
    sa.Column('description', sa.Text(), nullable=False),
    sa.Column('is_assignable', sa.Boolean(), server_default='true', nullable=False),
    sa.PrimaryKeyConstraint('id', name=op.f('roles_pkey')),
    sa.UniqueConstraint('code', name=op.f('roles_code_key'))
    )
    op.create_table('account_logins',
    sa.Column('login', sa.String(length=64), nullable=False),
    sa.Column('account_id', sa.Uuid(), nullable=False),
    sa.Column('password_hash', sa.Text(), nullable=False),
    sa.Column('recovery_hash', sa.LargeBinary(), nullable=False),
    sa.Column('updated_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.ForeignKeyConstraint(['account_id'], ['accounts.id'], name=op.f('account_logins_account_id_fkey')),
    sa.PrimaryKeyConstraint('login', name=op.f('account_logins_pkey')),
    sa.UniqueConstraint('account_id', name=op.f('account_logins_account_id_key'))
    )
    op.create_table('account_roles',
    sa.Column('account_id', sa.Uuid(), nullable=False),
    sa.Column('role_id', sa.Integer(), nullable=False),
    sa.Column('granted_by', sa.Uuid(), nullable=True),
    sa.Column('granted_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.Column('revoked_at', sa.DateTime(timezone=True), nullable=True),
    sa.Column('id', sa.Uuid(), nullable=False),
    sa.ForeignKeyConstraint(['account_id'], ['accounts.id'], name=op.f('account_roles_account_id_fkey')),
    sa.ForeignKeyConstraint(['granted_by'], ['accounts.id'], name=op.f('account_roles_granted_by_fkey')),
    sa.ForeignKeyConstraint(['role_id'], ['roles.id'], name=op.f('account_roles_role_id_fkey')),
    sa.PrimaryKeyConstraint('id', name=op.f('account_roles_pkey'))
    )
    op.create_index('uq_account_roles_active', 'account_roles', ['account_id', 'role_id'], unique=True, postgresql_where=sa.text('revoked_at IS NULL'))
    op.create_table('admin_audit_logs',
    sa.Column('actor_account_id', sa.Uuid(), nullable=True),
    sa.Column('target_account_id', sa.Uuid(), nullable=True),
    sa.Column('action', sa.String(length=128), nullable=False),
    sa.Column('result', sa.String(length=32), nullable=False),
    sa.Column('details', postgresql.JSONB(astext_type=sa.Text()), server_default='{}', nullable=False),
    sa.Column('id', sa.Uuid(), nullable=False),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.ForeignKeyConstraint(['actor_account_id'], ['accounts.id'], name=op.f('admin_audit_logs_actor_account_id_fkey')),
    sa.ForeignKeyConstraint(['target_account_id'], ['accounts.id'], name=op.f('admin_audit_logs_target_account_id_fkey')),
    sa.PrimaryKeyConstraint('id', name=op.f('admin_audit_logs_pkey'))
    )
    op.create_table('auth_identities',
    sa.Column('account_id', sa.Uuid(), nullable=False),
    sa.Column('provider', sa.String(length=32), nullable=False),
    sa.Column('issuer', sa.String(length=512), nullable=False),
    sa.Column('subject', sa.String(length=255), nullable=False),
    sa.Column('id', sa.Uuid(), nullable=False),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.ForeignKeyConstraint(['account_id'], ['accounts.id'], name=op.f('auth_identities_account_id_fkey')),
    sa.PrimaryKeyConstraint('id', name=op.f('auth_identities_pkey')),
    sa.UniqueConstraint('issuer', 'subject', name=op.f('auth_identities_issuer_subject_key'))
    )
    op.create_index(op.f('ix_auth_identities_account_id'), 'auth_identities', ['account_id'], unique=False)
    op.create_table('auth_sessions',
    sa.Column('account_id', sa.Uuid(), nullable=False),
    sa.Column('access_hash', sa.LargeBinary(), nullable=False),
    sa.Column('access_expires_at', sa.DateTime(timezone=True), nullable=False),
    sa.Column('refresh_hash', sa.LargeBinary(), nullable=False),
    sa.Column('refresh_expires_at', sa.DateTime(timezone=True), nullable=False),
    sa.Column('revoked_at', sa.DateTime(timezone=True), nullable=True),
    sa.Column('id', sa.Uuid(), nullable=False),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.ForeignKeyConstraint(['account_id'], ['accounts.id'], name=op.f('auth_sessions_account_id_fkey')),
    sa.PrimaryKeyConstraint('id', name=op.f('auth_sessions_pkey')),
    sa.UniqueConstraint('access_hash', name=op.f('auth_sessions_access_hash_key')),
    sa.UniqueConstraint('refresh_hash', name=op.f('auth_sessions_refresh_hash_key'))
    )
    op.create_index('auth_sessions_account_idx', 'auth_sessions', ['account_id'], unique=False)
    op.create_table('guest_credentials',
    sa.Column('token_hash', sa.LargeBinary(), nullable=False),
    sa.Column('account_id', sa.Uuid(), nullable=False),
    sa.Column('expires_at', sa.DateTime(timezone=True), nullable=False),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.ForeignKeyConstraint(['account_id'], ['accounts.id'], name=op.f('guest_credentials_account_id_fkey')),
    sa.PrimaryKeyConstraint('token_hash', name=op.f('guest_credentials_pkey'))
    )
    op.create_index('guest_credentials_account_idx', 'guest_credentials', ['account_id'], unique=False)
    op.create_table('oauth_login_attempts',
    sa.Column('provider', sa.String(length=32), nullable=False),
    sa.Column('account_id', sa.Uuid(), nullable=True),
    sa.Column('state_hash', sa.LargeBinary(), nullable=False),
    sa.Column('nonce_hash', sa.LargeBinary(), nullable=False),
    sa.Column('status', sa.String(length=16), server_default='PENDING', nullable=False),
    sa.Column('expires_at', sa.DateTime(timezone=True), nullable=False),
    sa.Column('completed_at', sa.DateTime(timezone=True), nullable=True),
    sa.Column('id', sa.Uuid(), nullable=False),
    sa.Column('created_at', sa.DateTime(timezone=True), server_default=sa.text('now()'), nullable=False),
    sa.CheckConstraint("status <> 'SUCCEEDED' OR (account_id IS NOT NULL AND completed_at IS NOT NULL)", name=op.f('ck_oauth_login_attempts_successful_result')),
    sa.CheckConstraint("status IN ('PENDING','SUCCEEDED','FAILED','EXPIRED')", name=op.f('ck_oauth_login_attempts_status')),
    sa.CheckConstraint('expires_at > created_at', name=op.f('ck_oauth_login_attempts_expiry')),
    sa.CheckConstraint('octet_length(state_hash) = 32 AND octet_length(nonce_hash) = 32', name=op.f('ck_oauth_login_attempts_hash_lengths')),
    sa.ForeignKeyConstraint(['account_id'], ['accounts.id'], name=op.f('oauth_login_attempts_account_id_fkey')),
    sa.PrimaryKeyConstraint('id', name=op.f('oauth_login_attempts_pkey')),
    sa.UniqueConstraint('nonce_hash', name=op.f('oauth_login_attempts_nonce_hash_key')),
    sa.UniqueConstraint('state_hash', name=op.f('oauth_login_attempts_state_hash_key'))
    )
    op.create_table('role_permissions',
    sa.Column('role_id', sa.Integer(), nullable=False),
    sa.Column('permission_id', sa.Integer(), nullable=False),
    sa.ForeignKeyConstraint(['permission_id'], ['permissions.id'], name=op.f('role_permissions_permission_id_fkey')),
    sa.ForeignKeyConstraint(['role_id'], ['roles.id'], name=op.f('role_permissions_role_id_fkey')),
    sa.PrimaryKeyConstraint('role_id', 'permission_id', name=op.f('role_permissions_pkey'))
    )


def downgrade() -> None:
    op.drop_table('role_permissions')
    op.drop_table('oauth_login_attempts')
    op.drop_index('guest_credentials_account_idx', table_name='guest_credentials')
    op.drop_table('guest_credentials')
    op.drop_index('auth_sessions_account_idx', table_name='auth_sessions')
    op.drop_table('auth_sessions')
    op.drop_index(op.f('ix_auth_identities_account_id'), table_name='auth_identities')
    op.drop_table('auth_identities')
    op.drop_table('admin_audit_logs')
    op.drop_index('uq_account_roles_active', table_name='account_roles', postgresql_where=sa.text('revoked_at IS NULL'))
    op.drop_table('account_roles')
    op.drop_table('account_logins')
    op.drop_table('roles')
    op.drop_table('permissions')
    op.drop_table('auth_attempts')
    op.drop_table('accounts')
