import os
from ..persistence.postgres.database import environment_url
from dataclasses import dataclass, field


@dataclass(frozen=True)
class Settings:
    database_url: str = field(repr=False)
    service_secret: str = field(repr=False)
    auth_url: str = "http://127.0.0.1:8081"
    game_url: str = "http://127.0.0.1:8080"
    wallet_url: str = "http://127.0.0.1:8082"
    inventory_url: str = "http://127.0.0.1:8083"
    app_environment: str = 'production'
    mock_payments_enabled: bool = False
    mock_grant_secret: str = field(default='', repr=False)

    auth_transport: str = 'http'
    auth_grpc_target: str = '127.0.0.1:50051'
    auth_grpc_timeout_ms: int = 2000
    auth_grpc_attempts: int = 3
    auth_grpc_ca_file: str = ''

    def __post_init__(self):
        from .auth_grpc import validate_auth_settings
        validate_auth_settings(self)
        if type(self.mock_payments_enabled) is not bool:
            raise ValueError('Invalid inventory MOCK flag')
        if self.mock_payments_enabled and (self.app_environment not in ('test','development')
                or not 32 <= len(self.mock_grant_secret) <= 256 or self.mock_grant_secret == self.service_secret):
            raise ValueError('Inventory MOCK requires test/development and a separate test secret')

    @classmethod
    def from_environment(cls):
        secret = os.environ["VD_SERVICE_SECRET"]
        if len(secret) < 32:
            raise ValueError("VD_SERVICE_SECRET must be at least 32 characters")
        flag = os.getenv('VD_INVENTORY_MOCK_PAYMENTS_ENABLED','0')
        if flag not in ('0','1'):raise ValueError('VD_INVENTORY_MOCK_PAYMENTS_ENABLED must be 0 or 1')
        return cls(environment_url("VD_INVENTORY"), secret,
                   os.getenv("VD_AUTH_URL", "http://127.0.0.1:8081"),
                   os.getenv("VD_GAME_URL", "http://127.0.0.1:8080"),
                   os.getenv("VD_WALLET_URL", "http://127.0.0.1:8082"),
                   os.getenv("VD_INVENTORY_URL", "http://127.0.0.1:8083"),
                   app_environment=os.getenv('VD_APP_ENVIRONMENT','production'),mock_payments_enabled=flag=='1',
                   mock_grant_secret=os.getenv('VD_MOCK_GRANT_SECRET',''),
                   auth_transport=os.getenv('VD_AUTH_TRANSPORT', 'grpc'),
                   auth_grpc_target=os.getenv('VD_AUTH_GRPC_TARGET', '127.0.0.1:50051'),
                   auth_grpc_timeout_ms=int(os.getenv('VD_AUTH_GRPC_TIMEOUT_MS', '2000')),
                   auth_grpc_attempts=int(os.getenv('VD_AUTH_GRPC_ATTEMPTS', '3')),
                   auth_grpc_ca_file=os.getenv('VD_AUTH_GRPC_CA_FILE', ''))