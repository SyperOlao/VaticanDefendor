import hashlib
import json
import re
from uuid import UUID

from .access import administrator
from .catalog import ACTIVE_STATES, ReceiptStatus, PurchaseState
from .errors import ApplicationError
from .ports import AuthGateway, UnitOfWorkFactory
from .use_cases import public_receipt
from ..domain.purchase_cursor import decode_cursor, encode_cursor


def recoverable(row):
    plan = row.receipt.get("plan")
    return bool(row.job and row.job["state"] == PurchaseState.REVIEW and row.receipt.get("status") == ReceiptStatus.REVIEW
        and row.receipt.get("_resumeState") in ACTIVE_STATES
        and isinstance(plan, dict) and set(plan) == {"price", "configurationId"} and type(plan.get("price")) is int
        and 0 <= plan["price"] <= 9_223_372_036_854_775_807
        and isinstance(plan.get("configurationId"), str)
        and re.fullmatch(r"[A-Za-z0-9_.-]{1,80}", plan["configurationId"]) is not None)


def inspection_response(row, *, detailed=True):
    result = {"accountId": str(row.account_id), "operationId": str(row.operation_id), "offerId": row.offer_id,
              "createdAt": row.created_at.isoformat(), "status": row.receipt.get("status"),
              "job": row.job, "canRetry": recoverable(row)}
    if detailed:
        result.update(catalogVersion=row.catalog_version, receipt=public_receipt(row.receipt),
                      plan=row.receipt.get("plan"), resumeState=row.receipt.get("_resumeState"))
    return result


class AdminPurchases:
    def __init__(self, uow: UnitOfWorkFactory, auth: AuthGateway):
        self.uow, self.auth = uow, auth

    def list(self, token, limit=50, before=None, status=None, account_id=None):
        administrator(self.auth, token)
        if (type(limit) is not int or not 1 <= limit <= 100
                or status is not None and (not isinstance(status, str) or status not in ReceiptStatus)
                or account_id is not None and not isinstance(account_id, UUID)):
            raise ApplicationError(422, "Invalid purchase filters")
        try:
            cursor = decode_cursor(before)
        except ValueError:
            raise ApplicationError(422, "Invalid purchase cursor") from None
        with self.uow() as work:
            rows = work.repository.list_operations(limit + 1, cursor, status, account_id)
        return {"purchases": [inspection_response(row, detailed=False) for row in rows[:limit]],
                "nextCursor": encode_cursor(rows[limit - 1].created_at, rows[limit - 1].account_id,
                                            rows[limit - 1].operation_id) if len(rows) > limit else None}

    def get(self, token, account_id, operation_id):
        administrator(self.auth, token)
        with self.uow() as work:
            row = work.repository.inspect(account_id, operation_id)
            if row is None:
                raise ApplicationError(404, "Purchase operation not found")
        return inspection_response(row)

    def events(self, token, account_id, operation_id, after=0, limit=50):
        administrator(self.auth, token)
        if type(after) is not int or not 0 <= after <= 9_223_372_036_854_775_807 or type(limit) is not int or not 1 <= limit <= 100:
            raise ApplicationError(422, "Invalid event pagination")
        with self.uow() as work:
            if work.repository.inspect(account_id, operation_id) is None:
                raise ApplicationError(404, "Purchase operation not found")
            rows = work.repository.events(account_id, operation_id, after, limit + 1)
        return {"events": rows[:limit], "nextCursor": rows[limit - 1]["sequence"] if len(rows) > limit else None}

    def retry(self, token, account_id, operation_id, expected_version, request_key, reason):
        actor = administrator(self.auth, token)
        if (not all(isinstance(value, UUID) for value in (account_id, operation_id, request_key))
                or type(expected_version) is not int or not 1 <= expected_version <= 9_223_372_036_854_775_807
                or not isinstance(reason, str) or not reason.strip() or len(reason) > 500
                or any((ord(char) < 32 and char not in "\n\t") or 0xD800 <= ord(char) <= 0xDFFF for char in reason)):
            raise ApplicationError(422, "Expected a version, request key and reconciliation reason")
        reason = reason.strip()
        digest = hashlib.sha256(json.dumps([str(account_id), str(operation_id), expected_version, reason],
            separators=(",", ":"), ensure_ascii=False).encode()).digest()
        with self.uow() as work:
            repo = work.repository
            repo.lock_player(actor)
            prior = repo.admin_command(actor, request_key, digest)
            if prior is not None:
                return {**prior, "replayed": True}
            row = repo.inspect(account_id, operation_id)
            if row is None:
                raise ApplicationError(404, "Purchase operation not found")
            if not recoverable(row):
                raise ApplicationError(409, "Purchase is not recoverable from Review")
            if not repo.requeue(account_id, operation_id, expected_version=expected_version,
                                actor_id=actor, reason=reason, request_key=request_key):
                raise ApplicationError(409, "Purchase state or version changed")
            row = repo.inspect(account_id, operation_id)
            result = {"accountId": str(account_id), "operationId": str(operation_id),
                      "status": ReceiptStatus.PENDING, "jobVersion": row.job["version"], "replayed": False}
            repo.record_admin_command(actor, request_key, digest, result)
            return result
