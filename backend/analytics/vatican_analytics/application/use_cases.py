from uuid import UUID
from .catalog import ProjectionStatus, MessageStatus
from .errors import ApplicationError
from .ports import UnitOfWorkFactory, ProjectionGateway
from ..domain.events import EVENT, EVENT_FIELDS, parse, digest, receipt


class AnalyticsUseCases:
    def __init__(self, uow: UnitOfWorkFactory):
        self.uow=uow

    def receive(self,message_id,event_name,schema_version,payload):
        try:
            if event_name not in EVENT_FIELDS or type(schema_version) is not int or schema_version!=1 or not isinstance(message_id,UUID):
                raise ValueError()
            parse(payload,event_name)
        except (ValueError,TypeError):
            raise ApplicationError(422,'Invalid analytics message') from None
        with self.uow() as work:
            row=work.repository.accept(message_id,UUID(payload[EVENT_FIELDS[event_name]]),UUID(payload['accountId']),digest(payload,event_name),payload,event_name)
            state=(MessageStatus.APPLIED if row['status']==ProjectionStatus.COMPLETED else
                   MessageStatus.REVIEW if row['status']==ProjectionStatus.REVIEW else MessageStatus.PENDING)
            return {'messageId':str(message_id),'status':state,'receipt':row['receipt']}

    def get(self,source_id,event_name=EVENT):
        with self.uow() as work:
            state=work.repository.get(source_id,event_name)
            if state is None:
                raise ApplicationError(404,'Analytics source not found')
            return state


class ProjectEvents:
    def __init__(self,uow: UnitOfWorkFactory,projection: ProjectionGateway):
        self.uow,self.projection=uow,projection

    def once(self):
        with self.uow() as work:
            row=work.repository.claim()
        if row is None:return False
        try:
            parse(row['payload'],row['eventName'])
            self.projection.project(row['payload'])
            with self.uow() as work:
                work.repository.confirm(row['sourceId'],row['lease'],receipt(row['payload'],row['eventName']),row['eventName'])
        except Exception:
            with self.uow() as work:
                work.repository.retry(row['sourceId'],row['lease'],row['eventName'])
            raise
        return True

    def requeue(self,source_id,event_name=EVENT):
        with self.uow() as work:
            return work.repository.requeue(source_id,event_name)
