"""Versioned ledger analytics contract; an event never causes another balance change."""
import hashlib
import json
from uuid import UUID, uuid5

ECONOMY_EVENT = "wallet.transaction.analytics"
MAX_DELIVERY_ATTEMPTS = 20


def message_id(transaction_id):
    return uuid5(transaction_id, "wallet.analytics.v1")


def payload_for(transaction):
    return {"transactionId": str(transaction.id), "accountId": str(transaction.account_id),
            "currencyId": transaction.currency_code, "delta": transaction.delta,
            "balanceAfter": transaction.balance_after, "reason": transaction.reason,
            "sourceType": transaction.source_type, "sourceId": str(transaction.source_id),
            "itemIndex": transaction.line_index, "occurredAt": transaction.created_at.isoformat()}


def expected_receipt(payload):
    digest = hashlib.sha256(json.dumps([ECONOMY_EVENT, 1, payload], sort_keys=True,
                                      separators=(",", ":")).encode()).hexdigest()
    return {"transactionId": payload["transactionId"], "accountId": payload["accountId"],
            "payloadHash": digest, "eventIds": [str(uuid5(UUID(payload["transactionId"]),
                                                        "analytics.wallet.economy_transaction.v1"))]}


def validate_receipt(payload, receipt):
    if not isinstance(receipt, dict) or receipt != expected_receipt(payload):
        raise ValueError("Invalid wallet analytics acknowledgement")
