"""Auth ownership and database constraints, without an API or another service."""
import os
from pathlib import Path
import subprocess
import sys
from datetime import datetime, timedelta, timezone
from uuid import uuid4

import pytest
from sqlalchemy import func, select
from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import Session

from vatican_auth.persistence.postgres.tables import (
    Account, AccountLogin, AccountRole, AuthIdentity, AuthSession, OAuthLoginAttempt, Role,
)


def test_auth_imports_without_other_services_and_has_only_local_foreign_keys():
    source = """
import sys
from vatican_auth.persistence.postgres import Base
assert not any(name.startswith(('vatican_backend', 'vatican_analytics', 'fastapi')) for name in sys.modules)
assert set(Base.metadata.tables) == {
    'accounts', 'account_logins', 'guest_credentials', 'auth_sessions', 'auth_identities',
    'auth_attempts', 'oauth_login_attempts', 'roles', 'permissions', 'role_permissions',
    'account_roles', 'admin_audit_logs',
}
for table in Base.metadata.tables.values():
    for key in table.foreign_keys:
        assert key.column.table.metadata is Base.metadata
"""
    directory = Path(__file__).parents[1]
    subprocess.run([sys.executable, "-c", source], cwd=directory,
                   env={**os.environ, "PYTHONPATH": str(directory)}, check=True)


def test_credentials_have_one_owner_and_unique_login(storage):
    first, second = uuid4(), uuid4()
    with Session(storage) as session, session.begin():
        session.add_all([Account(id=first), Account(id=second)])
        session.flush()
        session.add(AccountLogin(account_id=first, login="player", password_hash="argon2-hash", recovery_hash=b"r" * 32))
    for account_id, login in [(first, "other"), (second, "player"), (uuid4(), "orphan")]:
        with pytest.raises(IntegrityError), Session(storage) as session, session.begin():
            session.add(AccountLogin(account_id=account_id, login=login, password_hash="argon2-hash", recovery_hash=b"r" * 32))


def test_external_identity_cannot_attach_to_two_accounts(storage):
    first, second = uuid4(), uuid4()
    with Session(storage) as session, session.begin():
        session.add_all([Account(id=first), Account(id=second)])
        session.flush()
        session.add(AuthIdentity(account_id=first, provider="GOOGLE", issuer="https://accounts.example", subject="same-subject"))
    with pytest.raises(IntegrityError), Session(storage) as session, session.begin():
        session.add(AuthIdentity(account_id=second, provider="OTHER", issuer="https://accounts.example", subject="same-subject"))
    with Session(storage) as session, session.begin():
        session.add(AuthIdentity(account_id=second, provider="OTHER", issuer="https://other.example", subject="same-subject"))


def test_revoked_role_can_be_granted_again_but_active_pair_is_unique(storage):
    account_id = uuid4()
    with Session(storage) as session, session.begin():
        session.add(Account(id=account_id))
        session.add(Role(id=1, code="ADMIN", description="Admin"))
        session.flush()
        session.add(AccountRole(account_id=account_id, role_id=1))
    with pytest.raises(IntegrityError), Session(storage) as session, session.begin():
        session.add(AccountRole(account_id=account_id, role_id=1))
    with Session(storage) as session, session.begin():
        session.scalar(select(AccountRole)).revoked_at = datetime.now(timezone.utc)
        session.flush()
        session.add(AccountRole(account_id=account_id, role_id=1))
    with Session(storage) as session:
        assert session.scalar(select(func.count()).select_from(AccountRole)) == 2


def test_session_tokens_are_unique_and_require_an_account(storage):
    account_id = uuid4()
    expires = datetime.now(timezone.utc) + timedelta(hours=1)
    with Session(storage) as session, session.begin():
        session.add(Account(id=account_id))
        session.flush()
        session.add(AuthSession(account_id=account_id, access_hash=b"a" * 32, refresh_hash=b"r" * 32,
                                access_expires_at=expires, refresh_expires_at=expires))
    for owner, access, refresh in [(account_id, b"a" * 32, b"s" * 32),
                                    (account_id, b"b" * 32, b"r" * 32),
                                    (uuid4(), b"c" * 32, b"t" * 32)]:
        with pytest.raises(IntegrityError), Session(storage) as session, session.begin():
            session.add(AuthSession(account_id=owner, access_hash=access, refresh_hash=refresh,
                                    access_expires_at=expires, refresh_expires_at=expires))


def test_oauth_state_cannot_be_replayed_or_succeed_without_an_account(storage):
    values = dict(provider="GOOGLE", state_hash=b"s" * 32, nonce_hash=b"n" * 32,
                  expires_at=datetime.now(timezone.utc) + timedelta(minutes=5))
    with Session(storage) as session, session.begin():
        session.add(OAuthLoginAttempt(**values))
    for change in [{"nonce_hash": b"m" * 32},
                   {"state_hash": b"t" * 32, "nonce_hash": b"m" * 32, "status": "SUCCEEDED",
                    "completed_at": datetime.now(timezone.utc)}]:
        with pytest.raises(IntegrityError), Session(storage) as session, session.begin():
            session.add(OAuthLoginAttempt(**(values | change)))
