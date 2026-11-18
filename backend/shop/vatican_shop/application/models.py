from dataclasses import dataclass
from datetime import datetime
from uuid import UUID


@dataclass(frozen=True)
class Principal:
    account_id: UUID
    is_guest: bool
    roles: tuple[str, ...] = ()


@dataclass(frozen=True)
class OfferRecord:
    content: dict
    revision: int
    created_at: datetime
    updated_at: datetime
    deleted_at: datetime | None

    def response(self):
        return {**self.content, "revision": self.revision, "createdAt": self.created_at.isoformat(),
                "updatedAt": self.updated_at.isoformat(),
                "deletedAt": self.deleted_at.isoformat() if self.deleted_at else None}


@dataclass(frozen=True)
class PurchaseRecord:
    operation_id: UUID
    offer_id: str
    catalog_version: str
    receipt_json: dict
    created_at: datetime


@dataclass(frozen=True)
class PurchaseInspection:
    account_id: UUID
    operation_id: UUID
    offer_id: str
    catalog_version: str
    created_at: datetime
    receipt: dict
    job: dict | None
