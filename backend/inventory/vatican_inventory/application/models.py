from dataclasses import dataclass
from uuid import UUID


@dataclass(frozen=True)
class Principal:
    account_id: UUID
    is_guest: bool
