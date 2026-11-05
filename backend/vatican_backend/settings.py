import json
import os
import ipaddress
import re
from pathlib import Path
from dataclasses import dataclass, field
from uuid import UUID


@dataclass(frozen=True)
class ServerSlot:
    server_id: UUID
    host: str
    port: int
    secret: str
    transport: str = "udp-loopback"
    certificate_name: str = ""


@dataclass(frozen=True)
class RewardPolicy:
    per_church_level: int


@dataclass(frozen=True)
class Settings:
    database_url: str
    servers: dict[UUID, ServerSlot]
    grant_ttl_seconds: int
    reward_policies: dict[str, RewardPolicy] = field(default_factory=dict)
    pool_size: int = 10
    content_admin_secret: str = ""
    shop_catalog: dict = field(default_factory=dict)

    @classmethod
    def from_environment(cls) -> "Settings":
        database_url = os.environ["VD_DATABASE_URL"]
        slots = json.loads(os.environ["VD_SERVER_POOL_JSON"])
        if not isinstance(slots, list) or not slots:
            raise ValueError("VD_SERVER_POOL_JSON must contain at least one controlled server")
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
        from .shop import validate_catalog
        catalog_path = os.getenv("VD_SHOP_CATALOG_FILE")
        if catalog_path and os.getenv("VD_SHOP_CATALOG_JSON"):
            raise ValueError("Use only one shop catalog source")
        catalog = validate_catalog(json.loads(Path(catalog_path).read_text(encoding="utf-8")
                                              if catalog_path else os.getenv("VD_SHOP_CATALOG_JSON", "{}")))
        return cls(database_url, servers, ttl, policies, pool_size, admin, catalog)
