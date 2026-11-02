import json
import os
import ipaddress
import re
from dataclasses import dataclass, field
from uuid import UUID


@dataclass(frozen=True)
class ServerSlot:
    server_id: UUID
    host: str
    port: int
    secret: str


@dataclass(frozen=True)
class RewardPolicy:
    per_church_level: int


@dataclass(frozen=True)
class Settings:
    database_url: str
    servers: dict[UUID, ServerSlot]
    grant_ttl_seconds: int
    reward_policies: dict[str, RewardPolicy] = field(default_factory=dict)

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
            )
            if (not ipaddress.ip_address(item.host).is_loopback or
                    not 1 <= item.port <= 65535 or not 32 <= len(item.secret) <= 120):
                raise ValueError("Server host/port/secret is invalid")
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
        return cls(database_url, servers, ttl, policies)
