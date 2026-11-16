from dataclasses import dataclass
from datetime import timedelta


@dataclass(frozen=True)
class AuthPolicy:
    access_ttl: timedelta = timedelta(minutes=15)
    refresh_ttl: timedelta = timedelta(days=30)
    guest_ttl: timedelta = timedelta(days=30)
    attempt_window: timedelta = timedelta(minutes=5)
    ip_attempt_limit: int = 60
    login_attempt_limit: int = 10
    password_min_length: int = 8
    password_max_length: int = 128
    password_concurrency: int = 2

    def __post_init__(self):
        if any(value <= timedelta(0) for value in (
            self.access_ttl, self.refresh_ttl, self.guest_ttl, self.attempt_window,
        )):
            raise ValueError("Auth lifetimes must be positive")
        if self.refresh_ttl < self.access_ttl:
            raise ValueError("Refresh lifetime must cover access lifetime")
        if min(self.ip_attempt_limit, self.login_attempt_limit, self.password_concurrency) < 1:
            raise ValueError("Auth limits must be positive")
        if not 1 <= self.password_min_length <= self.password_max_length <= 1024:
            raise ValueError("Invalid password length policy")
