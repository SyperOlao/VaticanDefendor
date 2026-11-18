from enum import StrEnum

class InventoryTransport(StrEnum):
    HTTP = "http"
    RABBITMQ = "rabbitmq"

class PurchaseState(StrEnum):
    PENDING = "PENDING"
    CHARGED = "CHARGED"
    REFUND_PENDING = "REFUND_PENDING"
    COMPLETED = "COMPLETED"
    REJECTED = "REJECTED"
    REVIEW = "REVIEW"


class ReceiptStatus(StrEnum):
    PENDING = "Pending"
    COMPLETED = "Completed"
    REJECTED = "Rejected"
    REVIEW = "Review"


ACTIVE_STATES = (PurchaseState.PENDING, PurchaseState.CHARGED, PurchaseState.REFUND_PENDING)
RESERVED_STATUSES = (ReceiptStatus.PENDING, ReceiptStatus.COMPLETED, ReceiptStatus.REVIEW)

ROLE_MAP = {"admin": "ADMIN"}
MAX_OFFERS = 200


class CatalogAction(StrEnum):
    CREATE = "shop.offer.create"
    UPDATE = "shop.offer.update"
    DELETE = "shop.offer.delete"


class PurchaseEventKind(StrEnum):
    CREATED = "CREATED"
    CLAIMED = "CLAIMED"
    CHARGED = "CHARGED"
    COMPLETED = "COMPLETED"
    REFUND_PENDING = "REFUND_PENDING"
    REJECTED = "REJECTED"
    RETRY_SCHEDULED = "RETRY_SCHEDULED"
    REVIEW = "REVIEW"
    ADMIN_RETRY = "ADMIN_RETRY"
    CLI_RETRY = "CLI_RETRY"
    LEGACY_SNAPSHOT = "LEGACY_SNAPSHOT"


ADMIN_RETRY_OPERATION = "shop.purchase.retry"
DELIVERY_UNCONFIRMED = "DeliveryUnconfirmed"
MAX_PURCHASE_ATTEMPTS = 20
