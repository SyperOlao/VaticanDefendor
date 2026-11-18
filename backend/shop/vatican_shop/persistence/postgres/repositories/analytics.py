from datetime import datetime, timedelta, timezone
from uuid import uuid4
from sqlalchemy import select, update
from ....domain.analytics import AnalyticsStatus, MAX_DELIVERY_ATTEMPTS, PURCHASE_EVENT, message_id, payload_for, purchase_id
from ..tables import OutboxMessage


def now():return datetime.now(timezone.utc)


class AnalyticsRepository:
    def enqueue_purchase(self,operation,receipt,completed_at):
        payload=payload_for(operation.account_id,operation.operation_id,operation.offer_id,operation.catalog_version,
                            operation.receipt_json['plan'],receipt,completed_at)
        self.session.add(OutboxMessage(id=message_id(operation.account_id,operation.operation_id),destination='analytics',
            event_name=PURCHASE_EVENT,schema_version=1,aggregate_id=purchase_id(operation.account_id,operation.operation_id),payload=payload))

    def analytics_delivery(self,account_id,operation_id):
        row=self.session.get(OutboxMessage,message_id(account_id,operation_id))
        if row is None:return {'status':AnalyticsStatus.SKIPPED,'messageId':None,'receipt':None}
        status=AnalyticsStatus.COMPLETED if row.published_at else AnalyticsStatus.REVIEW if row.failed_at else AnalyticsStatus.PENDING
        return {'status':status,'messageId':str(row.id),'attempts':row.attempts,'lastError':row.last_error,'receipt':row.receipt_json}

    def claim_analytics(self):
        row=self.session.scalar(select(OutboxMessage).where(OutboxMessage.event_name==PURCHASE_EVENT,
            OutboxMessage.destination=='analytics',OutboxMessage.published_at.is_(None),OutboxMessage.failed_at.is_(None),
            OutboxMessage.available_at<=now()).order_by(OutboxMessage.created_at,OutboxMessage.id).limit(1).with_for_update(skip_locked=True))
        if row is None:return None
        if row.attempts>=MAX_DELIVERY_ATTEMPTS:
            # The last sender can die after delivery and before storing its acknowledgement.
            row.lease_token=None;row.failed_at=now();row.last_error='DeliveryUnconfirmed'
            return {'exhausted':True}
        row.lease_token=uuid4();row.attempts+=1;row.available_at=now()+timedelta(seconds=30)
        return {'id':row.id,'event_name':row.event_name,'schema_version':row.schema_version,'payload':row.payload,'lease':row.lease_token}

    def confirm_analytics(self,identity,lease,receipt):
        changed=self.session.scalar(update(OutboxMessage).where(OutboxMessage.id==identity,OutboxMessage.lease_token==lease,
            OutboxMessage.published_at.is_(None),OutboxMessage.failed_at.is_(None),OutboxMessage.event_name==PURCHASE_EVENT)
            .values(published_at=now(),lease_token=None,last_error=None,receipt_json=receipt).returning(OutboxMessage.id))
        return changed is not None

    def retry_analytics(self,identity,lease,permanent=False):
        row=self.session.get(OutboxMessage,identity,with_for_update=True)
        if row is None or row.event_name!=PURCHASE_EVENT or row.lease_token!=lease or row.published_at:return
        row.lease_token=None;row.last_error='DeliveryRejected' if permanent else 'DeliveryUnavailable'
        if permanent or row.attempts>=MAX_DELIVERY_ATTEMPTS:row.failed_at=now()
        else:row.available_at=now()+timedelta(seconds=min(300,2**min(row.attempts,8)))

    def requeue_analytics(self,account_id,operation_id):
        row=self.session.get(OutboxMessage,message_id(account_id,operation_id),with_for_update=True)
        if row is None or row.event_name!=PURCHASE_EVENT or row.published_at or not row.failed_at:return False
        row.failed_at=None;row.lease_token=None;row.attempts=0;row.last_error=None;row.available_at=now()
        return True
