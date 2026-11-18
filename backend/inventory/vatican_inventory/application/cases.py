"""Atomic, source-idempotent case debit; rejections are durable outcomes too."""
import hashlib
import json
import re
from uuid import UUID, uuid5
from .catalog import EventName, MessageStatus
from .errors import ApplicationError
from .ports import UnitOfWorkFactory


class CaseFundingConsumer:
    def __init__(self, uow: UnitOfWorkFactory):
        self.uow = uow

    def receive(self, message_id, event_name, schema_version, payload):
        try:
            if (event_name != EventName.CASE_FUNDING or type(schema_version) is not int or schema_version != 1
                    or not isinstance(payload,dict) or set(payload) != {'accountId','openingId','caseCode','quantity'}):
                raise ValueError()
            account, opening = UUID(payload['accountId']), UUID(payload['openingId'])
            code = payload['caseCode']
            if not isinstance(code,str) or re.fullmatch(r'[A-Za-z0-9_.-]{1,64}',code) is None:
                raise ValueError()
            if type(payload['quantity']) is not int or payload['quantity'] != 1:
                raise ValueError()
            digest = hashlib.sha256(json.dumps([event_name,schema_version,payload],sort_keys=True,separators=(',',':')).encode()).digest()
        except (ValueError,TypeError,KeyError,AttributeError):
            raise ApplicationError(422,'Invalid case funding message') from None
        with self.uow() as work:
            repo = work.repository
            repo.lock_message(message_id)
            repo.lock_player(uuid5(opening,'case.funding'))
            repo.lock_player(account)
            consumed = repo.received(message_id,digest)
            receipt = repo.case_receipt(opening,-1,account,digest)
            if receipt is None:
                if consumed:
                    raise ApplicationError(409,'Case funding message has no source receipt')
                repo.ensure(account)
                item_id = 'case:' + code
                quantity = repo.quantity(account, item_id)
                result = {'itemId': item_id, 'quantityAfter': quantity}
                if quantity < 1:
                    result.update(outcome=MessageStatus.REJECTED, code='InsufficientCases')
                else:
                    repo.set_quantity(account, item_id, quantity - 1)
                    result.update(outcome=MessageStatus.APPLIED, quantityAfter=quantity - 1,
                                  inventoryRevision=repo.state(account).revision)
                receipt = {**payload, **result}
                repo.record_case_receipt(opening,-1,account,digest,receipt)
            if not consumed:
                repo.record_message(message_id,digest)
            return {'messageId':str(message_id),'status':MessageStatus.APPLIED,'receipt':receipt}
