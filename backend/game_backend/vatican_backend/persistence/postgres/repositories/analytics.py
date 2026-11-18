from ....domain.analytics import analytics_message_id, AnalyticsStatus
from ....application.catalog import RewardDestination
from ..tables import OutboxMessage


def enqueue(session,source_id,event_name,payload):
    session.add(OutboxMessage(id=analytics_message_id(source_id,event_name),destination=RewardDestination.ANALYTICS,
        event_name=event_name,schema_version=1,aggregate_id=source_id,payload=payload))


def delivery(session,source_id,event_name):
    row=session.get(OutboxMessage,analytics_message_id(source_id,event_name))
    if row is None:return {'status':AnalyticsStatus.SKIPPED,'messageId':None,'receipt':None}
    status=AnalyticsStatus.COMPLETED if row.published_at else AnalyticsStatus.REVIEW if row.failed_at else AnalyticsStatus.PENDING
    return {'status':status,'messageId':str(row.id),'attempts':row.attempts,'lastError':row.last_error,'receipt':row.receipt_json}
