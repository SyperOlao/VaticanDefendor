import os
import re
from ..persistence.postgres.database import environment_url
from dataclasses import dataclass, field


@dataclass(frozen=True)
class Settings:
    database_url: str = field(repr=False)
    service_secret: str = field(repr=False)
    auth_grpc_target: str = "127.0.0.1:50051"
    game_url: str = "http://127.0.0.1:8080"
    wallet_url: str = "http://127.0.0.1:8082"
    inventory_url: str = "http://127.0.0.1:8083"
    pool_size: int = 10
    lock_timeout_ms: int = 1000
    statement_timeout_ms: int = 5000
    auth_grpc_timeout_ms: int = 2000
    auth_grpc_attempts: int = 3
    auth_grpc_ca_file: str = ''

    def __post_init__(self):
        if not isinstance(self.database_url, str) or not self.database_url:
            raise ValueError('Rating database URL is required')
        if (not isinstance(self.service_secret, str) or not 32 <= len(self.service_secret) <= 512
                or not self.service_secret.isascii() or not self.service_secret.isprintable()
                or any(c.isspace() for c in self.service_secret)):
            raise ValueError('VD_SERVICE_SECRET must contain 32..512 visible ASCII characters without whitespace')
        target = (re.fullmatch(r'(?:[A-Za-z0-9_.-]+|\[[0-9A-Fa-f:]+\]):([0-9]{1,5})', self.auth_grpc_target)
                  if isinstance(self.auth_grpc_target, str) else None)
        if target is None or not 1 <= int(target[1]) <= 65535:
            raise ValueError('VD_AUTH_GRPC_TARGET must be host:port, without an HTTP URL')
        for name, lower, upper in [('pool_size', 1, 100), ('lock_timeout_ms', 1, 60000),
                                   ('statement_timeout_ms', 1, 120000),
                                   ('auth_grpc_timeout_ms', 50, 30000), ('auth_grpc_attempts', 1, 5)]:
            value = getattr(self, name)
            if type(value) is not int or not lower <= value <= upper:
                raise ValueError(f'Invalid rating {name}')
        if self.statement_timeout_ms < self.lock_timeout_ms:
            raise ValueError('Rating statement timeout must be at least the lock timeout')

    @classmethod
    def from_environment(cls):
        secret = os.environ["VD_SERVICE_SECRET"]
        return cls(environment_url("VD_RATING"), secret,
                   os.getenv("VD_AUTH_GRPC_TARGET", "127.0.0.1:50051"),
                   os.getenv("VD_GAME_URL", "http://127.0.0.1:8080"),
                   os.getenv("VD_WALLET_URL", "http://127.0.0.1:8082"),
                   os.getenv("VD_INVENTORY_URL", "http://127.0.0.1:8083"),
                   pool_size=int(os.getenv('VD_RATING_DB_POOL_SIZE', '10')),
                   lock_timeout_ms=int(os.getenv('VD_RATING_DB_LOCK_TIMEOUT_MS', '1000')),
                   statement_timeout_ms=int(os.getenv('VD_RATING_DB_STATEMENT_TIMEOUT_MS', '5000')),
                   auth_grpc_timeout_ms=int(os.getenv('VD_AUTH_GRPC_TIMEOUT_MS', '2000')),
                   auth_grpc_attempts=int(os.getenv('VD_AUTH_GRPC_ATTEMPTS', '3')),
                   auth_grpc_ca_file=os.getenv('VD_AUTH_GRPC_CA_FILE', ''))
