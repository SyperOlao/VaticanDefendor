import os
from ..persistence.postgres.database import environment_url
from dataclasses import dataclass, field
from .rabbit import RabbitSettings, environment_rabbit_url
from ..application.catalog import InventoryTransport
from ..domain.mock_payments import MockPolicy


@dataclass(frozen=True)
class Settings:
    database_url: str = field(repr=False)
    service_secret: str = field(repr=False)
    auth_url: str = "http://127.0.0.1:8081"
    game_url: str = "http://127.0.0.1:8080"
    wallet_url: str = "http://127.0.0.1:8082"
    inventory_url: str = "http://127.0.0.1:8083"
    s3_endpoint_url: str = "http://127.0.0.1:19002"
    s3_bucket: str = "vatican-shop-media"
    s3_access_key: str = field(default="", repr=False)
    s3_secret_key: str = field(default="", repr=False)
    s3_region: str = "us-east-1"
    media_base_url: str = "http://127.0.0.1:19002/vatican-shop-media"

    analytics_url: str = "http://127.0.0.1:8086"
    inventory_transport: str = InventoryTransport.HTTP
    rabbitmq_url: str = field(default="", repr=False)
    rabbit_queue_prefix: str = "vatican.inventory"
    rabbit_exchange_prefix: str = "vatican"
    inventory_read_transport: str = 'http'
    inventory_grpc_target: str = '127.0.0.1:50051'
    inventory_grpc_ca_file: str = ''
    app_environment: str = 'production'
    mock_payments_enabled: bool = False
    mock_control_secret: str = field(default='',repr=False)
    mock_webhook_secret: str = field(default='',repr=False)
    mock_receipt_secret: str = field(default='',repr=False)
    mock_grant_secret: str = field(default='',repr=False)

    auth_transport: str = 'http'
    auth_grpc_target: str = '127.0.0.1:50051'
    auth_grpc_timeout_ms: int = 2000
    auth_grpc_attempts: int = 3
    auth_grpc_ca_file: str = ''

    wallet_transport: str = 'http'
    wallet_grpc_target: str = '127.0.0.1:50052'
    wallet_grpc_ca_file: str = ''

    def __post_init__(self):
        if self.wallet_transport not in ('http', 'rabbitmq'):
            raise ValueError('VD_WALLET_TRANSPORT must be http or rabbitmq')
        if self.wallet_transport == 'rabbitmq' and (not self.wallet_grpc_target or '://' in self.wallet_grpc_target):
            raise ValueError('Invalid wallet gRPC target')
        from .auth_grpc import validate_auth_settings
        validate_auth_settings(self)
        if self.inventory_read_transport not in ('http', 'grpc'):
            raise ValueError('Invalid inventory read transport')
        if self.inventory_read_transport == 'grpc' and (not self.inventory_grpc_target or '://' in self.inventory_grpc_target):
            raise ValueError('Invalid inventory gRPC target')
        InventoryTransport(self.inventory_transport)
        if self.inventory_transport == InventoryTransport.RABBITMQ:
            RabbitSettings(self.rabbitmq_url, self.rabbit_queue_prefix, self.rabbit_exchange_prefix)
        policy=MockPolicy(self.mock_payments_enabled,self.app_environment,self.mock_control_secret,
                          self.mock_webhook_secret,self.mock_receipt_secret,self.mock_grant_secret)
        if policy.enabled and self.service_secret in (policy.control_secret,policy.webhook_secret,policy.receipt_secret,policy.grant_secret):
            raise ValueError('MOCK test secrets must differ from the service secret')

    @classmethod
    def from_environment(cls):
        secret = os.environ["VD_SERVICE_SECRET"]
        if len(secret) < 32:
            raise ValueError("VD_SERVICE_SECRET must be at least 32 characters")
        transport = os.getenv("VD_SHOP_INVENTORY_TRANSPORT", InventoryTransport.HTTP)
        flag = os.getenv('VD_SHOP_MOCK_PAYMENTS_ENABLED','0')
        if flag not in ('0','1'):raise ValueError('VD_SHOP_MOCK_PAYMENTS_ENABLED must be 0 or 1')
        return cls(environment_url("VD_SHOP"), secret,
                   os.getenv("VD_AUTH_URL", "http://127.0.0.1:8081"),
                   os.getenv("VD_GAME_URL", "http://127.0.0.1:8080"),
                   os.getenv("VD_WALLET_URL", "http://127.0.0.1:8082"),
                   os.getenv("VD_INVENTORY_URL", "http://127.0.0.1:8083"),
                   os.getenv("VD_SHOP_S3_ENDPOINT_URL", "http://127.0.0.1:19002"),
                   os.getenv("VD_SHOP_S3_BUCKET", "vatican-shop-media"),
                   os.getenv("VD_SHOP_S3_ACCESS_KEY", ""), os.getenv("VD_SHOP_S3_SECRET_KEY", ""),
                   os.getenv("VD_SHOP_S3_REGION", "us-east-1"),
                   os.getenv("VD_SHOP_MEDIA_BASE_URL", "http://127.0.0.1:19002/vatican-shop-media"),
                   os.getenv("VD_ANALYTICS_URL", "http://127.0.0.1:8086"),
                   inventory_transport=transport,
                   rabbitmq_url=environment_rabbit_url() if transport == InventoryTransport.RABBITMQ else "",
                   rabbit_queue_prefix=os.getenv("VD_INVENTORY_RABBIT_QUEUE_PREFIX", "vatican.inventory"),
                   rabbit_exchange_prefix=os.getenv("VD_INVENTORY_RABBIT_EXCHANGE_PREFIX", "vatican"),
                   inventory_read_transport=os.getenv('VD_INVENTORY_READ_TRANSPORT', 'http'),
                   inventory_grpc_target=os.getenv('VD_INVENTORY_GRPC_TARGET', '127.0.0.1:50051'),
                   inventory_grpc_ca_file=os.getenv('VD_INVENTORY_GRPC_CA_FILE', ''),
                   app_environment=os.getenv('VD_APP_ENVIRONMENT','production'),mock_payments_enabled=flag=='1',
                   mock_control_secret=os.getenv('VD_MOCK_CONTROL_SECRET',''),mock_webhook_secret=os.getenv('VD_MOCK_WEBHOOK_SECRET',''),
                   mock_receipt_secret=os.getenv('VD_MOCK_RECEIPT_SECRET',''),mock_grant_secret=os.getenv('VD_MOCK_GRANT_SECRET',''),
                   wallet_transport=os.getenv('VD_WALLET_TRANSPORT', 'rabbitmq'),
                   wallet_grpc_target=os.getenv('VD_WALLET_GRPC_TARGET', '127.0.0.1:50052'),
                   wallet_grpc_ca_file=os.getenv('VD_WALLET_GRPC_CA_FILE', ''),
                   auth_transport=os.getenv('VD_AUTH_TRANSPORT', 'grpc'),
                   auth_grpc_target=os.getenv('VD_AUTH_GRPC_TARGET', '127.0.0.1:50051'),
                   auth_grpc_timeout_ms=int(os.getenv('VD_AUTH_GRPC_TIMEOUT_MS', '2000')),
                   auth_grpc_attempts=int(os.getenv('VD_AUTH_GRPC_ATTEMPTS', '3')),
                   auth_grpc_ca_file=os.getenv('VD_AUTH_GRPC_CA_FILE', ''))