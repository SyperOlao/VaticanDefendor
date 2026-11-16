from collections.abc import Mapping
from dataclasses import dataclass, field
from datetime import timedelta
import os
from urllib.parse import quote

from ..application.policy import AuthPolicy


@dataclass(frozen=True)
class AuthSettings:
    database_url: str = field(repr=False)
    pool_size: int = 10
    policy: AuthPolicy = field(default_factory=AuthPolicy)

    def __post_init__(self):
        if not self.database_url or self.pool_size < 1:
            raise ValueError("Auth database URL and positive pool size are required")

    @classmethod
    def from_environment(cls, environment: Mapping[str, str] | None = None) -> "AuthSettings":
        env = os.environ if environment is None else environment
        url = env.get("VD_AUTH_DATABASE_URL")
        if not url:
            password = env.get("VD_AUTH_DB_PASSWORD")
            if not password:
                raise ValueError("Set VD_AUTH_DATABASE_URL or VD_AUTH_DB_PASSWORD")
            user = quote(env.get("VD_AUTH_DB_USER", "auth"), safe="")
            password = quote(password, safe="")
            host = env.get("VD_AUTH_DB_HOST", "127.0.0.1")
            port = int(env.get("VD_AUTH_DB_PORT", "5432"))
            name = quote(env.get("VD_AUTH_DB_NAME", "vatican_auth"), safe="")
            url = f"postgresql://{user}:{password}@{host}:{port}/{name}"
        policy = AuthPolicy(
            access_ttl=timedelta(seconds=int(env.get("VD_AUTH_ACCESS_TTL_SECONDS", "900"))),
            refresh_ttl=timedelta(seconds=int(env.get("VD_AUTH_REFRESH_TTL_SECONDS", "2592000"))),
            guest_ttl=timedelta(seconds=int(env.get("VD_AUTH_GUEST_TTL_SECONDS", "2592000"))),
            attempt_window=timedelta(seconds=int(env.get("VD_AUTH_ATTEMPT_WINDOW_SECONDS", "300"))),
            ip_attempt_limit=int(env.get("VD_AUTH_IP_ATTEMPT_LIMIT", "60")),
            login_attempt_limit=int(env.get("VD_AUTH_LOGIN_ATTEMPT_LIMIT", "10")),
            password_min_length=int(env.get("VD_AUTH_PASSWORD_MIN_LENGTH", "8")),
            password_max_length=int(env.get("VD_AUTH_PASSWORD_MAX_LENGTH", "128")),
            password_concurrency=int(env.get("VD_AUTH_PASSWORD_CONCURRENCY", "2")),
        )
        return cls(url, int(env.get("VD_AUTH_DB_POOL_SIZE", "10")), policy)
