import math
from uuid import UUID, uuid4

from .catalog import ACCOUNT_STATUS_MAP
from .errors import InvalidCredentials, RateLimitExceeded
from .models import AccountRecord, SessionTokens
from .policy import AuthPolicy
from .ports import AuthUnitOfWork, Clock, TokenService, UnitOfWorkFactory
from .validation import normalize_ip


def require_active(account: AccountRecord | None) -> None:
    if account is None or account.status != ACCOUNT_STATUS_MAP["active"]:
        raise InvalidCredentials("Invalid or expired credential")


class AttemptLimiter:
    def __init__(self, uow: UnitOfWorkFactory, tokens: TokenService, clock: Clock, policy: AuthPolicy):
        self.uow, self.tokens, self.clock, self.policy = uow, tokens, clock, policy

    async def check(self, client_ip: str, login: str | None = None) -> None:
        keys = [(self.tokens.key_hash("ip:" + normalize_ip(client_ip)), self.policy.ip_attempt_limit)]
        if login is not None:
            keys.append((self.tokens.key_hash("login:" + login), self.policy.login_attempt_limit))
        retry_after = 0
        # These counters must survive a failed login or a later business transaction rollback.
        async with self.uow() as work:
            for key, limit in sorted(keys):
                now = self.clock.now()
                result = await work.attempts.record(key, now, self.policy.attempt_window)
                if result.attempts > limit:
                    remaining = (result.window_start + self.policy.attempt_window - now).total_seconds()
                    retry_after = max(retry_after, math.ceil(remaining), 1)
        if retry_after:
            raise RateLimitExceeded(retry_after)


class SessionIssuer:
    def __init__(self, tokens: TokenService, clock: Clock, policy: AuthPolicy):
        self.tokens, self.clock, self.policy = tokens, clock, policy

    async def issue(self, work: AuthUnitOfWork, account_id: UUID) -> SessionTokens:
        # Caller holds the account lock (or has just created the account) and checked its status.
        access, refresh = self.tokens.generate(), self.tokens.generate()
        now = self.clock.now()
        result = SessionTokens(account_id, uuid4(), access, refresh,
                               now + self.policy.access_ttl, now + self.policy.refresh_ttl)
        await work.sessions.create(result.session_id, account_id, self.tokens.digest(access), self.tokens.digest(refresh),
                                   result.access_expires_at, result.refresh_expires_at)
        return result


async def authenticate_in_work(work: AuthUnitOfWork, tokens: TokenService, clock: Clock,
                               token: str, *, lock: bool = False):
    from .models import Principal

    token_hash = tokens.digest(token)
    guest = await work.guests.get(token_hash)
    if guest is not None and guest.expires_at > clock.now():
        if guest.account_status != ACCOUNT_STATUS_MAP["active"]:
            raise InvalidCredentials("Invalid or expired credential")
        principal = Principal(guest.account_id, None, True)
    else:
        session = await work.sessions.by_access(token_hash)
        if (session is None or session.revoked_at is not None or session.account_status != ACCOUNT_STATUS_MAP["active"]
                or session.access_expires_at <= clock.now()):
            raise InvalidCredentials("Invalid or expired credential")
        principal = Principal(session.account_id, session.id, False)
    if lock:
        require_active(await work.accounts.get(principal.account_id, lock=True))
        return await authenticate_in_work(work, tokens, clock, token)
    return principal
