import hmac
from uuid import uuid4

from .errors import AccountAlreadyRegistered, InvalidCredentials, LoginUnavailable
from .models import CredentialResult, GuestTokens, Principal, SessionTokens
from .policy import AuthPolicy
from .ports import Clock, PasswordService, TokenService, UnitOfWorkFactory
from .services import AttemptLimiter, SessionIssuer, authenticate_in_work, require_active
from .validation import normalize_login, validate_password


class CreateGuestUseCase:
    def __init__(self, uow: UnitOfWorkFactory, tokens: TokenService, clock: Clock,
                 policy: AuthPolicy, limiter: AttemptLimiter):
        self.uow, self.tokens, self.clock, self.policy, self.limiter = uow, tokens, clock, policy, limiter

    async def execute(self, client_ip: str) -> GuestTokens:
        await self.limiter.check(client_ip)
        account_id, token = uuid4(), self.tokens.generate()
        expires_at = self.clock.now() + self.policy.guest_ttl
        async with self.uow() as work:
            await work.accounts.create(account_id)
            await work.guests.create(account_id, self.tokens.digest(token), expires_at)
        return GuestTokens(account_id, token, expires_at)


class CredentialUseCase:
    def __init__(self, uow: UnitOfWorkFactory, passwords: PasswordService, tokens: TokenService,
                 clock: Clock, policy: AuthPolicy, limiter: AttemptLimiter, issuer: SessionIssuer):
        self.uow, self.passwords, self.tokens = uow, passwords, tokens
        self.clock, self.policy, self.limiter, self.issuer = clock, policy, limiter, issuer


class RegisterUseCase(CredentialUseCase):
    async def execute(self, login: str, password: str, client_ip: str,
                      guest_token: str | None = None) -> CredentialResult:
        login = normalize_login(login)
        validate_password(password, self.policy)
        await self.limiter.check(client_ip, login)
        password_hash = await self.passwords.hash(password)
        recovery = self.tokens.generate()
        guest_hash = self.tokens.digest(guest_token) if guest_token is not None else None
        async with self.uow() as work:
            if guest_hash is None:
                account_id = uuid4()
            else:
                guest = await work.guests.get(guest_hash)
                if guest is None:
                    raise InvalidCredentials("Invalid or expired credential")
                account_id = guest.account_id
                require_active(await work.accounts.get(account_id, lock=True))
                guest = await work.guests.get(guest_hash)
                if guest is None or guest.expires_at <= self.clock.now():
                    raise InvalidCredentials("Invalid or expired credential")
                if await work.credentials.by_account(account_id) is not None:
                    raise AccountAlreadyRegistered("Account already has a login")
            await work.credentials.lock_login(login)
            if await work.credentials.by_login(login) is not None:
                raise LoginUnavailable("Login is unavailable")
            if guest_hash is None:
                await work.accounts.create(account_id)
            await work.credentials.create(account_id, login, password_hash, self.tokens.digest(recovery))
            await work.guests.delete(account_id)
            await work.sessions.revoke(account_id, self.clock.now())
            session = await self.issuer.issue(work, account_id)
        return CredentialResult(session, recovery)


class LoginUseCase(CredentialUseCase):
    async def execute(self, login: str, password: str, client_ip: str) -> SessionTokens:
        login = normalize_login(login)
        validate_password(password, self.policy, creating=False)
        await self.limiter.check(client_ip, login)
        async with self.uow() as work:
            previous = await work.credentials.by_login(login)
        # Hashing runs outside both the DB transaction and the asyncio event loop.
        check = await self.passwords.verify(password, previous.password_hash if previous else None)
        if previous is None or not check.valid:
            raise InvalidCredentials("Invalid login or password")
        async with self.uow() as work:
            require_active(await work.accounts.get(previous.account_id, lock=True))
            current = await work.credentials.by_login(login)
            if current is None or current.account_id != previous.account_id or current.password_hash != previous.password_hash:
                raise InvalidCredentials("Credentials changed; retry login")
            if check.replacement_hash is not None:
                await work.credentials.rehash(current.account_id, check.replacement_hash, self.clock.now())
            return await self.issuer.issue(work, current.account_id)


class RecoverUseCase(CredentialUseCase):
    async def execute(self, login: str, password: str, recovery_code: str, client_ip: str) -> CredentialResult:
        login = normalize_login(login)
        validate_password(password, self.policy)
        await self.limiter.check(client_ip, login)
        recovery_hash = self.tokens.digest(recovery_code)
        async with self.uow() as work:
            previous = await work.credentials.by_login(login)
        if previous is None or not hmac.compare_digest(previous.recovery_hash, recovery_hash):
            raise InvalidCredentials("Invalid login or recovery code")
        password_hash = await self.passwords.hash(password)
        next_code = self.tokens.generate()
        async with self.uow() as work:
            require_active(await work.accounts.get(previous.account_id, lock=True))
            current = await work.credentials.by_login(login)
            if current is None or not hmac.compare_digest(current.recovery_hash, recovery_hash):
                raise InvalidCredentials("Invalid login or recovery code")
            await work.credentials.update(current.account_id, password_hash, self.tokens.digest(next_code), self.clock.now())
            await work.sessions.revoke(current.account_id, self.clock.now())
            await work.guests.delete(current.account_id)
            session = await self.issuer.issue(work, current.account_id)
        return CredentialResult(session, next_code)


class AuthenticateUseCase:
    def __init__(self, uow: UnitOfWorkFactory, tokens: TokenService, clock: Clock):
        self.uow, self.tokens, self.clock = uow, tokens, clock

    async def execute(self, token: str) -> Principal:
        async with self.uow() as work:
            return await authenticate_in_work(work, self.tokens, self.clock, token)


class RefreshUseCase:
    def __init__(self, uow: UnitOfWorkFactory, tokens: TokenService, clock: Clock, issuer: SessionIssuer):
        self.uow, self.tokens, self.clock, self.issuer = uow, tokens, clock, issuer

    async def execute(self, refresh_token: str) -> SessionTokens:
        token_hash = self.tokens.digest(refresh_token)
        async with self.uow() as work:
            previous = await work.sessions.by_refresh(token_hash)
            if previous is None:
                raise InvalidCredentials("Invalid or expired credential")
            require_active(await work.accounts.get(previous.account_id, lock=True))
            current = await work.sessions.by_refresh(token_hash, lock=True)
            if current is None or current.revoked_at is not None or current.refresh_expires_at <= self.clock.now():
                raise InvalidCredentials("Invalid or expired credential")
            await work.sessions.revoke(current.account_id, self.clock.now(), current.id)
            return await self.issuer.issue(work, current.account_id)


class LogoutUseCase:
    def __init__(self, uow: UnitOfWorkFactory, tokens: TokenService, clock: Clock):
        self.uow, self.tokens, self.clock = uow, tokens, clock

    async def execute(self, access_token: str, *, all_sessions: bool = False) -> None:
        token_hash = self.tokens.digest(access_token)
        async with self.uow() as work:
            guest = await work.guests.get(token_hash)
            session = await work.sessions.by_access(token_hash) if guest is None else None
            if guest is None and session is None:
                raise InvalidCredentials("Invalid or expired credential")
            account_id = guest.account_id if guest else session.account_id
            require_active(await work.accounts.get(account_id, lock=True))
            # Re-read after acquiring the account lock: refresh/recovery may have revoked it.
            if guest is not None:
                guest = await work.guests.get(token_hash)
                if guest is None or guest.expires_at <= self.clock.now():
                    raise InvalidCredentials("Invalid or expired credential")
            else:
                session = await work.sessions.by_access(token_hash)
                if session is None or session.revoked_at is not None or session.access_expires_at <= self.clock.now():
                    raise InvalidCredentials("Invalid or expired credential")
            if all_sessions:
                await work.guests.delete(account_id)
                await work.sessions.revoke(account_id, self.clock.now())
            elif guest is not None:
                await work.guests.delete(account_id, token_hash)
            else:
                await work.sessions.revoke(account_id, self.clock.now(), session.id)
