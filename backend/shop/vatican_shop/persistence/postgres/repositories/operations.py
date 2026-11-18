from sqlalchemy import and_, func, select, tuple_

from ..tables import IdempotencyRecord, PurchaseEvent, PurchaseJob, PurchaseOperation
from ....application.catalog import ACTIVE_STATES, ADMIN_RETRY_OPERATION
from ....application.errors import ApplicationError
from ....application.models import PurchaseInspection


class OperationsQueries:
    @staticmethod
    def _inspection(operation, job):
        job_data = None
        if job is not None:
            job_data = {"state": job.state, "version": job.version, "attempts": job.attempts,
                "lastErrorCode": job.last_error_code,
                "leaseExpiresAt": job.available_at.isoformat() if job.lease_token else None,
                "nextAttemptAt": job.available_at.isoformat() if not job.lease_token and job.state in ACTIVE_STATES else None}
        return PurchaseInspection(operation.account_id, operation.operation_id, operation.offer_id,
                                  operation.catalog_version, operation.created_at, dict(operation.receipt_json), job_data)

    @staticmethod
    def _operations_query():
        return select(PurchaseOperation, PurchaseJob).outerjoin(PurchaseJob, and_(
            PurchaseJob.account_id == PurchaseOperation.account_id,
            PurchaseJob.operation_id == PurchaseOperation.operation_id))

    def inspect(self, account_id, operation_id):
        result = self.session.execute(self._operations_query().where(PurchaseOperation.account_id == account_id,
            PurchaseOperation.operation_id == operation_id).execution_options(populate_existing=True)).one_or_none()
        return self._inspection(*result) if result is not None else None

    def list_operations(self, limit, before, status, account_id):
        query = self._operations_query()
        if status is not None:
            query = query.where(PurchaseOperation.receipt_json["status"].astext == status)
        if account_id is not None:
            query = query.where(PurchaseOperation.account_id == account_id)
        if before is not None:
            query = query.where(tuple_(PurchaseOperation.created_at, PurchaseOperation.account_id,
                                       PurchaseOperation.operation_id) < before)
        query = query.order_by(PurchaseOperation.created_at.desc(), PurchaseOperation.account_id.desc(),
                               PurchaseOperation.operation_id.desc()).limit(limit)
        return [self._inspection(*row) for row in self.session.execute(query)]

    def events(self, account_id, operation_id, after, limit):
        query = select(PurchaseEvent).where(PurchaseEvent.account_id == account_id,
            PurchaseEvent.operation_id == operation_id, PurchaseEvent.sequence > after).order_by(PurchaseEvent.sequence).limit(limit)
        return [{"sequence": row.sequence, "kind": row.kind, "fromState": row.from_state, "state": row.state,
                 "attempts": row.attempts, "errorCode": row.error_code,
                 "actorId": str(row.actor_id) if row.actor_id else None,
                 "requestKey": str(row.request_key) if row.request_key else None, "reason": row.reason,
                 "createdAt": row.created_at.isoformat()} for row in self.session.scalars(query)]

    def admin_command(self, actor_id, request_key, digest):
        row = self.session.get(IdempotencyRecord, (actor_id, ADMIN_RETRY_OPERATION, str(request_key)))
        if row is None:
            return None
        if row.payload_hash != digest:
            raise ApplicationError(409, "Administrator command payload changed")
        return row.result_json

    def record_admin_command(self, actor_id, request_key, digest, result):
        self.session.add(IdempotencyRecord(account_id=actor_id, operation=ADMIN_RETRY_OPERATION,
            request_key=str(request_key), payload_hash=digest, status="COMPLETED", result_json=result, completed_at=func.now()))

    def _event(self, account_id, operation_id, sequence, kind, state, *, from_state=None, attempts=None,
               error_code=None, actor_id=None, reason=None, request_key=None):
        self.session.add(PurchaseEvent(account_id=account_id, operation_id=operation_id, sequence=sequence,
            kind=kind, state=state, from_state=from_state, attempts=attempts, error_code=error_code,
            actor_id=actor_id, reason=reason, request_key=request_key))
