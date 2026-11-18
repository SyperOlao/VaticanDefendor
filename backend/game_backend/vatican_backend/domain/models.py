from dataclasses import dataclass, field
from uuid import UUID


@dataclass(frozen=True)
class ServerSlot:
    server_id: UUID
    host: str
    port: int
    secret: str = field(repr=False)
    transport: str = "udp-loopback"
    certificate_name: str = ""


@dataclass(frozen=True)
class RewardPolicy:
    per_church_level: int


