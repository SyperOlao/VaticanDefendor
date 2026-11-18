"""Atomic, source-idempotent case debit; rejections are durable outcomes too."""
import hashlib
import json
import re
from uuid import UUID, uuid5
from .catalog import EventName
from .errors import ApplicationError
from .ports import UnitOfWorkFactory
from .catalog import CurrencyCode


class CaseFundingConsumer:
    def __init__(self, uow: UnitOfWorkFactory):
        self.uow = uow

    def receive(self, message_id, event_name, schema_version, payload):
        try:
            if (event_name != EventName.CASE_FUNDING or type(schema_version) is not int or schema_version != 1
                    or not isinstance(payload,dict) or set(payload) != {'accountId','openingId','caseCode','currencyId','amount'}):
                raise ValueError()
            account, opening = UUID(payload['accountId']), UUID(payload['openingId'])
            code = payload['caseCode']
            if not isinstance(code,str) or re.fullmatch(r'[A-Za-z0-9_.-]{1,64}',code) is None:
                raise ValueError()
            currency, amount = payload['currencyId'], payload['amount']
            if currency not in set(CurrencyCode) or type(amount) is not int or not 1 <= amount <= 9_223_372_036_854_775_807:
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
                receipt = {**payload,**repo.pay_case(account,opening,currency,amount)}
                repo.record_case_receipt(opening,-1,account,digest,receipt)
            if not consumed:
                repo.record_message(message_id,digest)
            return {'messageId':str(message_id),'status':'APPLIED','receipt':receipt}
