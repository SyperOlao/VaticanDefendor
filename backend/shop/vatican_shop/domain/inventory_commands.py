"""Public inventory protocol; no imports of the inventory implementation."""
from enum import StrEnum
from uuid import UUID, uuid5
import hashlib
import json


class InventoryCommandStatus(StrEnum):
    PENDING = "PENDING"
    APPLIED = "APPLIED"
    REJECTED = "REJECTED"
    REVIEW = "REVIEW"


GRANT_EVENT = "inventory.weapon.grant"


def command_id(account_id, operation_id):
    return uuid5(account_id, "shop.inventory.grant.v1:" + str(operation_id))


def grant_payload(job):
    return {"accountId": str(job["accountId"]), "operationId": str(job["operationId"]),
            "configurationId": job["plan"]["configurationId"]}


def grant_command(job):
    return {"messageId": str(command_id(job["accountId"], job["operationId"])),
            "eventName": GRANT_EVENT, "schemaVersion": 1, "payload": grant_payload(job)}


def validate_grant_receipt(job, receipt):
    if (not isinstance(receipt, dict) or receipt.get("status") not in ("APPLIED", "REJECTED")
            or any(type(receipt.get(key)) is not str or receipt[key] != value for key, value in grant_payload(job).items())):
        raise ValueError("Invalid grant receipt")
    if receipt["status"] == "REJECTED":
        if receipt.get("code") != "UnknownWeaponConfiguration":
            raise ValueError("Unknown grant rejection")
    else:
        if not isinstance(receipt.get("instanceId"), str) or UUID(receipt["instanceId"]).int == 0:
            raise ValueError("Invalid granted instance")
        if "inventoryRevision" in receipt and (type(receipt["inventoryRevision"]) is not int
                or not 1 <= receipt["inventoryRevision"] <= 9_223_372_036_854_775_807):
            raise ValueError("Invalid inventory revision")
    return receipt


def broker_result(job, body):
    identity = str(command_id(job["accountId"], job["operationId"]))
    if (not isinstance(body, dict) or body.get("messageId") != identity
            or body.get("status") not in InventoryCommandStatus
            or type(body.get("deliveryGeneration")) is not int or body["deliveryGeneration"] < 1):
        raise ValueError("Invalid inventory command status")
    expected = hashlib.sha256(json.dumps(grant_command(job), sort_keys=True, separators=(',', ':'), allow_nan=False).encode()).hexdigest()
    if 'payloadSha256' in body and body['payloadSha256'] != expected:
        raise ValueError('Inventory command payload mismatch')
    status, result = body["status"], body.get("result")
    if status == InventoryCommandStatus.PENDING:
        if result is not None:
            raise ValueError("Unconfirmed command has a result")
        return status, None
    if not isinstance(result, dict) or result.get("messageId") != identity or result.get("status") != status:
        raise ValueError("Inventory command result mismatch")
    return status, validate_grant_receipt(job, result.get("receipt")) if status == InventoryCommandStatus.APPLIED else None
