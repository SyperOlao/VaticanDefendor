import json
import os
from ..persistence.postgres.database import environment_url
import ipaddress
import re
from dataclasses import dataclass, field
from uuid import UUID


from ..domain.models import ServerSlot, RewardPolicy


@dataclass(frozen=True)
class Settings:
    database_url: str = field(repr=False)
    servers: dict[UUID, ServerSlot]
    grant_ttl_seconds: int
    reward_policies: dict[str, RewardPolicy] = field(default_factory=dict)
    pool_size: int = 10
    content_admin_secret: str = field(default="", repr=False)
    auth_url: str = "http://127.0.0.1:8081"
    wallet_url: str = "http://127.0.0.1:8082"
    inventory_url: str = "http://127.0.0.1:8083"
    service_secret: str = field(default="", repr=False)
    energy_ruleset_id: str = ""
    assignment_lease_seconds: int = 30
    rating_url: str = "http://127.0.0.1:8085"
    rating_board_code: str = "run-xp"
    analytics_url: str = "http://127.0.0.1:8086"
    avatar_catalog_file: str = "profile-avatars.json"
    rating_transport: str = 'http'
    prestige_ruleset_id: str = ''
    inventory_transport: str = 'http'
    inventory_read_transport: str = 'http'
    inventory_grpc_target: str = '127.0.0.1:50051'
    inventory_grpc_ca_file: str = ''
    inventory_rabbitmq_url: str = field(default='', repr=False)
    inventory_rabbit_queue_prefix: str = 'vatican.inventory'
    inventory_rabbit_exchange_prefix: str = 'vatican'

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
        if self.inventory_transport not in ('http', 'rabbitmq') or self.inventory_read_transport not in ('http', 'grpc'):
            raise ValueError('Invalid inventory transport')
        if self.inventory_read_transport == 'grpc' and (not self.inventory_grpc_target or '://' in self.inventory_grpc_target):
            raise ValueError('Invalid inventory gRPC target')
        if self.inventory_transport == 'rabbitmq':
            from .inventory_rabbit import RabbitSettings
            RabbitSettings(self.inventory_rabbitmq_url, self.inventory_rabbit_queue_prefix, self.inventory_rabbit_exchange_prefix)
        if self.prestige_ruleset_id and re.fullmatch(r'[A-Za-z0-9_.-]{1,128}',self.prestige_ruleset_id) is None:
            raise ValueError('VD_PRESTIGE_RULESET_ID is invalid')
        if self.rating_transport not in ('http', 'rabbitmq'):
            raise ValueError('VD_RATING_TRANSPORT must be http or rabbitmq')

    @classmethod
    def from_environment(cls) -> "Settings":
        database_url = environment_url("VD")
        slots = json.loads(os.getenv("VD_SERVER_POOL_JSON", "[]"))
        if not isinstance(slots, list):
            raise ValueError("VD_SERVER_POOL_JSON must be a list")
        servers = {}
        for slot in slots:
            item = ServerSlot(
                server_id=UUID(slot["serverId"]),
                host=str(slot["host"]),
                port=int(slot["port"]),
                secret=str(slot["secret"]),
                transport=str(slot.get("transport", "udp-loopback")),
                certificate_name=str(slot.get("certificateName", "")),
            )
            address = ipaddress.ip_address(item.host)
            if (address.version != 4 or address.is_unspecified or address.is_multicast or
                    not 1 <= item.port <= 65535 or not 32 <= len(item.secret) <= 120):
                raise ValueError("Server host/port/secret is invalid")
            if item.transport not in ("udp-loopback", "dtls"):
                raise ValueError("Unsupported game transport")
            if item.transport == "udp-loopback" and not address.is_loopback:
                raise ValueError("Remote game servers require DTLS")
            if item.transport == "dtls" and not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9.-]{0,252}", item.certificate_name):
                raise ValueError("DTLS requires a certificateName")
            if item.server_id in servers:
                raise ValueError("Duplicate serverId")
            servers[item.server_id] = item
        ttl = int(os.getenv("VD_GRANT_TTL_SECONDS", "120"))
        if not 10 <= ttl <= 600:
            raise ValueError("VD_GRANT_TTL_SECONDS must be 10..600")
        policies = {}
        for release_id, amounts in json.loads(os.getenv("VD_RUN_REWARDS_JSON", "{}" )).items():
            if re.fullmatch(r"sha256-[0-9a-f]{64}", release_id) is None:
                raise ValueError("Invalid reward policy release ID")
            if set(amounts) != {"perChurchLevel"} or any(
                not isinstance(value, int) or isinstance(value, bool) or value < 1 or value > 1000000
                for value in amounts.values()
            ):
                raise ValueError("Invalid reward policy amounts")
            policies[release_id] = RewardPolicy(amounts["perChurchLevel"])
        pool_size = int(os.getenv("VD_DB_POOL_SIZE", "10"))
        if not 1 <= pool_size <= 100:
            raise ValueError("VD_DB_POOL_SIZE must be 1..100")
        admin = os.getenv("VD_CONTENT_ADMIN_SECRET", "")
        if admin and len(admin) < 32:
            raise ValueError("VD_CONTENT_ADMIN_SECRET must be at least 32 characters")
        energy_ruleset = os.getenv("VD_ENERGY_RULESET_ID", "")
        if energy_ruleset and (len(energy_ruleset) > 128 or not re.fullmatch(r"[A-Za-z0-9_.-]+", energy_ruleset)):
            raise ValueError("VD_ENERGY_RULESET_ID is invalid")
        lease_seconds = int(os.getenv("VD_ASSIGNMENT_LEASE_SECONDS", "30"))
        if not 1 <= lease_seconds <= 300:
            raise ValueError("VD_ASSIGNMENT_LEASE_SECONDS must be 1..300")
        rating_board = os.getenv('VD_RATING_BOARD_CODE','run-xp')
        if re.fullmatch(r'[A-Za-z0-9_.-]{1,64}',rating_board) is None:
            raise ValueError('VD_RATING_BOARD_CODE is invalid')
        return cls(database_url, servers, ttl, policies, pool_size, admin,
                   os.getenv("VD_AUTH_URL", "http://127.0.0.1:8081"),
                   os.getenv("VD_WALLET_URL", "http://127.0.0.1:8082"),
                   os.getenv("VD_INVENTORY_URL", "http://127.0.0.1:8083"),
                   os.getenv("VD_SERVICE_SECRET", ""), energy_ruleset, lease_seconds, os.getenv('VD_RATING_URL','http://127.0.0.1:8085'), rating_board, os.getenv('VD_ANALYTICS_URL','http://127.0.0.1:8086'),
                   os.getenv('VD_PROFILE_AVATARS_FILE','profile-avatars.json'),
                   rating_transport=os.getenv('VD_RATING_TRANSPORT', 'http'),
                   prestige_ruleset_id=os.getenv('VD_PRESTIGE_RULESET_ID',''),
                   inventory_transport=os.getenv('VD_GAME_INVENTORY_TRANSPORT', 'http'),
                   inventory_read_transport=os.getenv('VD_INVENTORY_READ_TRANSPORT', 'http'),
                   inventory_grpc_target=os.getenv('VD_INVENTORY_GRPC_TARGET', '127.0.0.1:50051'),
                   inventory_grpc_ca_file=os.getenv('VD_INVENTORY_GRPC_CA_FILE', ''),
                   inventory_rabbitmq_url=_inventory_rabbit_url(),
                   inventory_rabbit_queue_prefix=os.getenv('VD_INVENTORY_RABBIT_QUEUE_PREFIX', 'vatican.inventory'),
                   inventory_rabbit_exchange_prefix=os.getenv('VD_INVENTORY_RABBIT_EXCHANGE_PREFIX', 'vatican'),
                   wallet_transport=os.getenv('VD_WALLET_TRANSPORT', 'rabbitmq'),
                   wallet_grpc_target=os.getenv('VD_WALLET_GRPC_TARGET', '127.0.0.1:50052'),
                   wallet_grpc_ca_file=os.getenv('VD_WALLET_GRPC_CA_FILE', ''),
                   auth_transport=os.getenv('VD_AUTH_TRANSPORT', 'grpc'),
                   auth_grpc_target=os.getenv('VD_AUTH_GRPC_TARGET', '127.0.0.1:50051'),
                   auth_grpc_timeout_ms=int(os.getenv('VD_AUTH_GRPC_TIMEOUT_MS', '2000')),
                   auth_grpc_attempts=int(os.getenv('VD_AUTH_GRPC_ATTEMPTS', '3')),
                   auth_grpc_ca_file=os.getenv('VD_AUTH_GRPC_CA_FILE', ''))


def _inventory_rabbit_url():
    if os.getenv('VD_GAME_INVENTORY_TRANSPORT', 'http') != 'rabbitmq':return ''
    from .inventory_rabbit import environment_rabbit_url
    return environment_rabbit_url()
