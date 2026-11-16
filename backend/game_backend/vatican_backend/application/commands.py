from dataclasses import dataclass
from uuid import UUID


@dataclass(frozen=True)
class ServerRegistration:
    content_release_id: str
    protocol_version: int
    transport: str


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
