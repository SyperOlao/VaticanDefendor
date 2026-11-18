from datetime import datetime, timedelta, timezone
from uuid import uuid4
from sqlalchemy import select, update, func
from ....application.catalog import ProjectionStatus
from ....domain.events import EVENT, EVENT_FIELDS
from ....application.errors import ApplicationError
from ..tables import AnalyticsInbox, ReceivedMessage


def now():
    return datetime.now(timezone.utc)


class InboxRepository:
    def __init__(self,session):
        self.session=session

    @staticmethod
    def _state(row):
        return {'sourceId':str(row.source_id),'eventName':row.event_name,EVENT_FIELDS[row.event_name]:str(row.source_id),'accountId':str(row.account_id),'status':row.status,'attempts':row.attempts,
                'lastError':row.last_error,'receipt':row.receipt_json,'completedAt':row.completed_at.isoformat() if row.completed_at else None}

    def accept(self,message_id,source_id,account_id,digest,payload,event_name=EVENT):
        # Both source and message alias are globally immutable. Acquire locks in one order.
        for key in sorted({'analytics.source:'+event_name+':'+str(source_id),'analytics.message:'+str(message_id)}):
            self.session.execute(select(func.pg_advisory_xact_lock(func.hashtextextended(key,0))))
        message=self.session.get(ReceivedMessage,message_id)
        if message and (message.event_name!=event_name or message.source_id!=source_id or message.payload_hash!=digest):
            raise ApplicationError(409,'Analytics message payload changed')
        row=self.session.get(AnalyticsInbox,(event_name,source_id))
        if row and (row.account_id!=account_id or row.payload_hash!=digest):
            raise ApplicationError(409,'Analytics source payload changed')
        if row is None:
            if message:raise ApplicationError(409,'Analytics message has no source')
            row=AnalyticsInbox(event_name=event_name,source_id=source_id,account_id=account_id,payload_hash=digest,payload_json=payload)
            self.session.add(row);self.session.flush()
        if message is None:
            self.session.add(ReceivedMessage(message_id=message_id,event_name=event_name,source_id=source_id,payload_hash=digest))
        return self._state(row)

    def get(self,source_id,event_name=EVENT):
        row=self.session.get(AnalyticsInbox,(event_name,source_id))
        return self._state(row) if row else None

    def claim(self):
        row=self.session.scalar(select(AnalyticsInbox).where(AnalyticsInbox.status==ProjectionStatus.PENDING,
            AnalyticsInbox.available_at<=now()).order_by(AnalyticsInbox.created_at,AnalyticsInbox.event_name,AnalyticsInbox.source_id)
            .limit(1).with_for_update(skip_locked=True))
        if row is None:return None
        row.lease_token=uuid4();row.attempts+=1;row.available_at=now()+timedelta(seconds=30)
        return {'sourceId':row.source_id,'eventName':row.event_name,**({'runId':row.source_id} if row.event_name==EVENT else {}),'payload':row.payload_json,'lease':row.lease_token}

    def confirm(self,source_id,lease,receipt,event_name=EVENT):
        changed=self.session.scalar(update(AnalyticsInbox).where(AnalyticsInbox.event_name==event_name,AnalyticsInbox.source_id==source_id,
            AnalyticsInbox.lease_token==lease,AnalyticsInbox.status==ProjectionStatus.PENDING)
            .values(status=ProjectionStatus.COMPLETED,lease_token=None,receipt_json=receipt,completed_at=now(),last_error=None)
            .returning(AnalyticsInbox.source_id))
        return changed is not None

    def retry(self,source_id,lease,event_name=EVENT):
        row=self.session.get(AnalyticsInbox,(event_name,source_id),with_for_update=True)
        if row is None or row.lease_token!=lease or row.status!=ProjectionStatus.PENDING:return
        row.lease_token=None;row.last_error='ProjectionUnavailable'
        if row.attempts>=20:row.status=ProjectionStatus.REVIEW
        else:row.available_at=now()+timedelta(seconds=min(300,2**min(row.attempts,8)))

    def requeue(self,source_id,event_name=EVENT):
        row=self.session.get(AnalyticsInbox,(event_name,source_id),with_for_update=True)
        if row is None or row.status!=ProjectionStatus.REVIEW:return False
        row.status=ProjectionStatus.PENDING;row.lease_token=None;row.attempts=0;row.last_error=None;row.available_at=now()
        return True
