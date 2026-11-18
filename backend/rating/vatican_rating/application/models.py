from dataclasses import dataclass
from uuid import UUID


@dataclass(frozen=True)
class Principal:
    account_id: UUID
    is_guest: bool


@dataclass(frozen=True)
class BoardDefinition:
    code: str
    score_source: str


@dataclass(frozen=True)
class RankedEntry:
    account_id: UUID
    score: int
    position: int


@dataclass(frozen=True)
class PlayerStanding:
    entry: RankedEntry | None
