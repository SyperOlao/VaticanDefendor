from dataclasses import dataclass, field
from ..domain.progression import RunStats
from uuid import UUID


@dataclass(frozen=True)
class ServerRegistration:
    content_release_id: str
    protocol_version: int
    transport: str
    ruleset_id: str | None = None
    prestige_schema_version: int = 0


@dataclass(frozen=True)
class Assignment:
    run_id: UUID
    generation: int


@dataclass(frozen=True)
class Checkpoint(Assignment):
    schema_version: int
    simulation_tick: int
    snapshot_json: str
    sha256: str


@dataclass(frozen=True)
class RunResult(Assignment):
    account_id: UUID
    content_release_id: str
    outcome: str
    church_level: int
    stats: RunStats = field(default_factory=RunStats)
    schema_version: int = 1
    server_id: UUID | None = None
    ruleset_id: str | None = None
    statistics: dict | None = None
