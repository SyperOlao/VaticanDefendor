"""One atomic PRESTIGE debit per immutable permanent-upgrade purchase."""
import hashlib,json,re
from uuid import UUID,uuid5
from .catalog import EventName,CurrencyCode
from .errors import ApplicationError
from .ports import UnitOfWorkFactory


class PrestigePaymentConsumer:
    def __init__(self,uow: UnitOfWorkFactory):self.uow=uow

    def receive(self,message_id,event_name,schema_version,payload):
        try:
            if (not isinstance(message_id,UUID) or event_name!=EventName.PRESTIGE_PAYMENT
                    or type(schema_version) is not int or schema_version!=1 or not isinstance(payload,dict)
                    or set(payload)!={'accountId','purchaseId','code','rulesetId','fromLevel','toLevel','amount','currencyId'}):raise ValueError()
            account,purchase=UUID(payload['accountId']),UUID(payload['purchaseId'])
            if (purchase.int==0 or payload['currencyId']!=CurrencyCode.PRESTIGE
                    or type(payload['amount']) is not int or not 1<=payload['amount']<=9_223_372_036_854_775_807
                    or type(payload['fromLevel']) is not int or not 0<=payload['fromLevel']<5
                    or type(payload['toLevel']) is not int or payload['toLevel']!=payload['fromLevel']+1
                    or any(not isinstance(payload[k],str) or re.fullmatch(pattern,payload[k]) is None for k,pattern in (
                        ('code',r'[A-Za-z0-9_.-]{1,64}'),('rulesetId',r'[A-Za-z0-9_.-]{1,128}')))):raise ValueError()
            digest=hashlib.sha256(json.dumps([event_name,1,payload],sort_keys=True,separators=(',',':')).encode()).digest()
        except (ValueError,TypeError,KeyError,AttributeError):raise ApplicationError(422,'Invalid prestige payment') from None
        with self.uow() as work:
            repo=work.repository
            # Global message alias first, then immutable source, then the player balance.
            repo.lock_message(message_id);repo.lock_player(uuid5(purchase,'prestige.payment'));repo.lock_player(account)
            consumed=repo.received(message_id,digest)
            receipt=repo.prestige_receipt(purchase,account,digest)
            if receipt is None:
                if consumed:raise ApplicationError(409,'Prestige payment has no source receipt')
                receipt={**payload,**repo.pay_prestige(account,purchase,payload['amount'])}
                repo.record_prestige_receipt(purchase,account,digest,receipt)
            if not consumed:repo.record_message(message_id,digest)
            return {'messageId':str(message_id),'status':'APPLIED','receipt':receipt}
