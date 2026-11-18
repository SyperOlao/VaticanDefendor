import hashlib
import json
import re
from uuid import UUID
from .catalog import MAX_SCORE, RatingEvent, MessageStatus
from .errors import ApplicationError
from .ports import UnitOfWorkFactory


class RunScoreConsumer:
    def __init__(self, uow: UnitOfWorkFactory):
        self.uow = uow

    def receive(self, message_id, event_name, schema_version, payload):
        try:
            message_id = UUID(str(message_id))
            if (event_name != RatingEvent.RUN or type(schema_version) is not int or schema_version != 1
                    or not isinstance(payload,dict) or set(payload) != {
                        'runId','accountId','boardCode','rulesetId','contentReleaseId','points'}):
                raise ValueError()
            run, account = UUID(payload['runId']), UUID(payload['accountId'])
            if (type(payload['points']) is not int or not 0 <= payload['points'] <= MAX_SCORE
                    or any(not isinstance(payload[field],str) or re.fullmatch(pattern,payload[field]) is None
                        for field,pattern in [('boardCode',r'[A-Za-z0-9_.-]{1,64}'),
                            ('rulesetId',r'[A-Za-z0-9_.-]{1,128}'),('contentReleaseId',r'[A-Za-z0-9_.-]{1,128}')])):
                raise ValueError()
            digest = hashlib.sha256(json.dumps([event_name,schema_version,payload],sort_keys=True,separators=(',',':')).encode()).digest()
        except (ValueError,TypeError,KeyError,AttributeError):
            raise ApplicationError(422,'Invalid run rating message') from None
        with self.uow() as work:
            repo = work.repository
            repo.lock_message(message_id)
            repo.lock_run(run)
            repo.lock_player(account)
            consumed = repo.received(message_id,digest)
            receipt = repo.run_receipt(run,account,digest)
            if receipt is None:
                if consumed:
                    raise ApplicationError(409,'Run rating message has no source receipt')
                receipt = {**payload,**repo.add_run_score(account,payload['boardCode'],payload['points'])}
                repo.record_run_receipt(run,account,payload['rulesetId'],payload['contentReleaseId'],payload['points'],digest,receipt)
            if not consumed:
                repo.record_message(message_id,digest)
            return {'messageId':str(message_id),'status':MessageStatus.APPLIED,'receipt':receipt}
