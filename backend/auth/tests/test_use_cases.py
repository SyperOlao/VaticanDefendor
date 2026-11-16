import ast
import asyncio
from dataclasses import replace
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pytest
from dishka import Provider, Scope
from sqlalchemy import func, select, update

from vatican_auth.application.errors import InvalidCredentials, InvalidInput, LoginUnavailable, RateLimitExceeded
from vatican_auth.application.models import SessionTokens
from vatican_auth.application.policy import AuthPolicy
from vatican_auth.application.ports import Clock, PasswordService
from vatican_auth.application.use_cases import (
    AuthenticateUseCase, CreateGuestUseCase, LoginUseCase, LogoutUseCase,
    RecoverUseCase, RefreshUseCase, RegisterUseCase,
)
from vatican_auth.bootstrap.di import create_auth_container
from vatican_auth.persistence.postgres.database import Database
from vatican_auth.persistence.postgres.repositories.sessions import PostgresSessionsRepository
from vatican_auth.persistence.postgres.tables import Account, AccountLogin, AuthAttempt, AuthSession, GuestCredential
from vatican_auth.security.tokens import SecureTokens
from vatican_auth.bootstrap.settings import AuthSettings

PASSWORD = "correct-password-123"
NEXT_PASSWORD = "replacement-password-456"
IP = "127.0.0.1"


class MutableClock:
    def __init__(self):
        self.value = datetime.now(timezone.utc)

    def now(self):
        return self.value


def run_case(storage, scenario, *, policy=None, pool_size=10, clock=None):
    async def run():
        settings = AuthSettings(storage.url.render_as_string(hide_password=False), pool_size, policy or AuthPolicy())
        providers = []
        if clock is not None:
            provider = Provider()
            provider.provide(lambda: clock, provides=Clock, scope=Scope.APP, override=True)
            providers.append(provider)
        container = create_auth_container(settings, *providers)
        try:
            await scenario(container, await container.get(Database))
        finally:
            await container.close()
    asyncio.run(run())


async def invoke(container, use_case, *args, **kwargs):
    async with container() as request:
        return await (await request.get(use_case)).execute(*args, **kwargs)


async def register(container, login="player", **kwargs):
    return await invoke(container, RegisterUseCase, login, PASSWORD, IP, **kwargs)


async def denied(container, use_case, *args, **kwargs):
    with pytest.raises(InvalidCredentials):
        await invoke(container, use_case, *args, **kwargs)


def test_guest_upgrade_preserves_id_and_revokes_guest_token(storage):
    async def scenario(container, database):
        guest = await invoke(container, CreateGuestUseCase, IP)
        principal = await invoke(container, AuthenticateUseCase, guest.token)
        assert principal.account_id == guest.account_id and principal.is_guest
        result = await register(container, "Player_One", guest_token=guest.token)
        assert result.session.account_id == guest.account_id
        await denied(container, AuthenticateUseCase, guest.token)
        principal = await invoke(container, AuthenticateUseCase, result.session.access_token)
        assert principal.account_id == guest.account_id and not principal.is_guest
        logged_in = await invoke(container, LoginUseCase, "PLAYER_ONE", PASSWORD, IP)
        assert logged_in.account_id == guest.account_id
        async with database.sessions() as session:
            assert await session.scalar(select(func.count()).select_from(Account)) == 1
            assert await session.scalar(select(func.count()).select_from(GuestCredential)) == 0
            row = (await session.execute(select(AccountLogin))).scalar_one()
            assert row.login == "player_one" and row.password_hash.startswith("$argon2id$")
            assert row.recovery_hash == SecureTokens().digest(result.recovery_code)
            assert PASSWORD not in row.password_hash
        assert result.recovery_code not in repr(result)
        assert result.session.access_token not in repr(result.session)
        assert guest.token not in repr(guest)
    run_case(storage, scenario)


def test_registration_race_has_one_winner_and_no_orphan_accounts(storage):
    async def scenario(container, database):
        results = await asyncio.gather(register(container, "Same_Login"), register(container, "SAME_LOGIN"),
                                       return_exceptions=True)
        assert sum(isinstance(result, LoginUnavailable) for result in results) == 1
        assert sum(not isinstance(result, BaseException) for result in results) == 1
        async with database.sessions() as session:
            assert await session.scalar(select(func.count()).select_from(Account)) == 1
            assert await session.scalar(select(func.count()).select_from(AccountLogin)) == 1
            assert await session.scalar(select(func.count()).select_from(AuthSession)) == 1
    run_case(storage, scenario)


def test_refresh_is_single_use_even_when_delivered_concurrently(storage):
    async def scenario(container, database):
        first = (await register(container)).session
        await denied(container, AuthenticateUseCase, first.refresh_token)
        await denied(container, RefreshUseCase, first.access_token)
        results = await asyncio.gather(
            invoke(container, RefreshUseCase, first.refresh_token),
            invoke(container, RefreshUseCase, first.refresh_token), return_exceptions=True,
        )
        assert sum(isinstance(result, InvalidCredentials) for result in results) == 1
        current = next(result for result in results if isinstance(result, SessionTokens))
        await denied(container, AuthenticateUseCase, first.access_token)
        assert (await invoke(container, AuthenticateUseCase, current.access_token)).account_id == first.account_id
        async with database.sessions() as session:
            assert await session.scalar(select(func.count()).select_from(AuthSession).where(AuthSession.revoked_at.is_(None))) == 1
    run_case(storage, scenario)


def test_failed_refresh_rolls_back_revocation_and_can_be_retried(storage, monkeypatch):
    async def scenario(container, database):
        first = (await register(container)).session
        original = PostgresSessionsRepository.create
        async def fail_after_insert(repository, *args, **kwargs):
            await original(repository, *args, **kwargs)
            raise RuntimeError("injected failure")
        with monkeypatch.context() as patch:
            patch.setattr(PostgresSessionsRepository, "create", fail_after_insert)
            with pytest.raises(RuntimeError, match="injected failure"):
                await invoke(container, RefreshUseCase, first.refresh_token)
        assert (await invoke(container, AuthenticateUseCase, first.access_token)).account_id == first.account_id
        current = await invoke(container, RefreshUseCase, first.refresh_token)
        assert current.session_id != first.session_id
        async with database.sessions() as session:
            assert await session.scalar(select(func.count()).select_from(AuthSession)) == 2
    run_case(storage, scenario)


def test_failed_guest_registration_preserves_guest_and_credentials_can_be_retried(storage, monkeypatch):
    async def scenario(container, database):
        guest = await invoke(container, CreateGuestUseCase, IP)
        original = PostgresSessionsRepository.create
        async def fail_after_insert(repository, *args, **kwargs):
            await original(repository, *args, **kwargs)
            raise RuntimeError("injected failure")
        with monkeypatch.context() as patch:
            patch.setattr(PostgresSessionsRepository, "create", fail_after_insert)
            with pytest.raises(RuntimeError):
                await register(container, guest_token=guest.token)
        assert (await invoke(container, AuthenticateUseCase, guest.token)).account_id == guest.account_id
        async with database.sessions() as session:
            assert await session.scalar(select(func.count()).select_from(AccountLogin)) == 0
            assert await session.scalar(select(func.count()).select_from(AuthSession)) == 0
        assert (await register(container, guest_token=guest.token)).session.account_id == guest.account_id
    run_case(storage, scenario)


def test_recovery_rotates_code_and_revokes_all_previous_sessions(storage):
    async def scenario(container, database):
        result = await register(container)
        other = await invoke(container, LoginUseCase, "player", PASSWORD, IP)
        recovered = await invoke(container, RecoverUseCase, "player", NEXT_PASSWORD, result.recovery_code, IP)
        assert recovered.recovery_code != result.recovery_code
        for previous in [result.session, other]:
            await denied(container, AuthenticateUseCase, previous.access_token)
            await denied(container, RefreshUseCase, previous.refresh_token)
        await denied(container, RecoverUseCase, "player", PASSWORD, result.recovery_code, IP)
        await denied(container, LoginUseCase, "player", PASSWORD, IP)
        current = await invoke(container, LoginUseCase, "player", NEXT_PASSWORD, IP)
        assert current.account_id == recovered.session.account_id
    run_case(storage, scenario)


def test_recovery_revokes_a_concurrently_refreshed_session(storage):
    async def scenario(container, database):
        result = await register(container)
        refresh, recovered = await asyncio.gather(
            invoke(container, RefreshUseCase, result.session.refresh_token),
            invoke(container, RecoverUseCase, "player", NEXT_PASSWORD, result.recovery_code, IP),
            return_exceptions=True,
        )
        assert not isinstance(recovered, BaseException)
        if isinstance(refresh, SessionTokens):
            await denied(container, AuthenticateUseCase, refresh.access_token)
            await denied(container, RefreshUseCase, refresh.refresh_token)
        else:
            assert isinstance(refresh, InvalidCredentials)
        assert (await invoke(container, AuthenticateUseCase, recovered.session.access_token)).account_id == result.session.account_id
        async with database.sessions() as session:
            assert await session.scalar(select(func.count()).select_from(AuthSession).where(AuthSession.revoked_at.is_(None))) == 1
    run_case(storage, scenario)


def test_password_verification_releases_connection_and_rechecks_credentials_after_recovery(storage, monkeypatch):
    async def scenario(container, database):
        result = await register(container)
        passwords = await container.get(PasswordService)
        verified, resume = asyncio.Event(), asyncio.Event()
        original = passwords.verify
        async def pause(password, encoded_hash):
            check = await original(password, encoded_hash)
            assert database.engine.pool.checkedout() == 0
            verified.set()
            await resume.wait()
            return check
        with monkeypatch.context() as patch:
            patch.setattr(passwords, "verify", pause)
            login = asyncio.create_task(invoke(container, LoginUseCase, "player", PASSWORD, IP))
            try:
                await asyncio.wait_for(verified.wait(), timeout=5)
                recovered = await invoke(container, RecoverUseCase, "player", NEXT_PASSWORD, result.recovery_code, IP)
            finally:
                resume.set()
            with pytest.raises(InvalidCredentials):
                await login
        assert (await invoke(container, AuthenticateUseCase, recovered.session.access_token)).account_id == result.session.account_id
    run_case(storage, scenario, pool_size=1)


def test_logout_revokes_one_session_then_all_sessions_and_guest_logout(storage):
    async def scenario(container, database):
        result = await register(container)
        other = await invoke(container, LoginUseCase, "player", PASSWORD, IP)
        await invoke(container, LogoutUseCase, result.session.access_token)
        await denied(container, RefreshUseCase, result.session.refresh_token)
        assert (await invoke(container, AuthenticateUseCase, other.access_token)).account_id == result.session.account_id
        await invoke(container, LogoutUseCase, other.access_token, all_sessions=True)
        await denied(container, AuthenticateUseCase, other.access_token)
        await denied(container, RefreshUseCase, other.refresh_token)
        guest = await invoke(container, CreateGuestUseCase, IP)
        await invoke(container, LogoutUseCase, guest.token)
        await denied(container, AuthenticateUseCase, guest.token)
    run_case(storage, scenario)


def test_inactive_account_cannot_authenticate_login_refresh_recover_or_upgrade(storage):
    async def scenario(container, database):
        guest = await invoke(container, CreateGuestUseCase, IP)
        result = await register(container)
        async with database.sessions.begin() as session:
            await session.execute(update(Account).values(account_status="BANNED"))
        await denied(container, AuthenticateUseCase, guest.token)
        await denied(container, AuthenticateUseCase, result.session.access_token)
        await denied(container, RefreshUseCase, result.session.refresh_token)
        await denied(container, LoginUseCase, "player", PASSWORD, IP)
        await denied(container, RecoverUseCase, "player", NEXT_PASSWORD, result.recovery_code, IP)
        await denied(container, RegisterUseCase, "guest_upgrade", PASSWORD, IP, guest_token=guest.token)
    run_case(storage, scenario)


def test_failed_logins_commit_rate_limit_and_window_expires(storage):
    clock = MutableClock()
    policy = replace(AuthPolicy(), login_attempt_limit=3)
    async def scenario(container, database):
        await register(container)
        for _ in range(2):
            await denied(container, LoginUseCase, "player", "wrong-password", IP)
        with pytest.raises(RateLimitExceeded) as error:
            await invoke(container, LoginUseCase, "player", PASSWORD, IP)
        assert 1 <= error.value.retry_after <= 300
        async with database.sessions() as session:
            attempts = (await session.execute(select(AuthAttempt))).scalars().all()
            assert len(attempts) == 2 and all(row.attempts == 4 for row in attempts)
            assert all(len(row.key_hash) == 32 for row in attempts)
        clock.value += timedelta(minutes=5)
        assert (await invoke(container, LoginUseCase, "player", PASSWORD, IP)).account_id
    run_case(storage, scenario, policy=policy, clock=clock)


def test_expiration_is_enforced_for_guest_access_and_refresh_tokens(storage):
    clock = MutableClock()
    policy = replace(AuthPolicy(), access_ttl=timedelta(seconds=1), guest_ttl=timedelta(seconds=1),
                     refresh_ttl=timedelta(seconds=2))
    async def scenario(container, database):
        guest = await invoke(container, CreateGuestUseCase, IP)
        result = await register(container)
        clock.value += timedelta(seconds=1)
        await denied(container, AuthenticateUseCase, guest.token)
        await denied(container, AuthenticateUseCase, result.session.access_token)
        await denied(container, RegisterUseCase, "upgrade", PASSWORD, IP, guest_token=guest.token)
        renewed = await invoke(container, RefreshUseCase, result.session.refresh_token)
        assert renewed.session_id != result.session.session_id
        clock.value += timedelta(seconds=2)
        await denied(container, RefreshUseCase, renewed.refresh_token)
    run_case(storage, scenario, policy=policy, clock=clock)


def test_invalid_inputs_and_unknown_user_do_not_create_accounts(storage):
    async def scenario(container, database):
        for login, password in [("bad login", PASSWORD), ("player", "short")]:
            with pytest.raises(InvalidInput):
                await invoke(container, RegisterUseCase, login, password, IP)
        await denied(container, LoginUseCase, "unknown", PASSWORD, IP)
        for token in ["", "x" * 43, "é" * 43, "a" * 44]:
            await denied(container, AuthenticateUseCase, token)
        async with database.sessions() as session:
            assert await session.scalar(select(func.count()).select_from(Account)) == 0
    run_case(storage, scenario)


def test_application_has_no_framework_orm_crypto_backend_or_container_imports():
    directory = Path(__file__).parents[1] / "vatican_auth/application"
    forbidden = {"fastapi", "starlette", "sqlalchemy", "psycopg", "dishka", "argon2", "persistence", "security"}
    for path in directory.glob("*.py"):
        for node in ast.walk(ast.parse(path.read_text())):
            if isinstance(node, ast.Import):
                names = {alias.name.split(".")[0] for alias in node.names}
            elif isinstance(node, ast.ImportFrom):
                names = {(node.module or "").split(".")[0]}
            else:
                continue
            assert not names & forbidden, path
