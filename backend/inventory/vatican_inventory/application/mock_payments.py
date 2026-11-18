import hashlib
import hmac
import json
import re
from uuid import UUID

from .ports import UnitOfWorkFactory, DefinitionsGateway
from .errors import ApplicationError
from ..domain.weapons import definition_map


class MockPaymentGrants:
    def __init__(self, uow: UnitOfWorkFactory, definitions: DefinitionsGateway,
                 enabled=False, environment='production', grant_secret=''):
        self.uow, self.definitions = uow, definitions
        self.enabled, self.environment, self.grant_secret = enabled, environment, grant_secret

    def grant(self, payload, credential):
        if not self.enabled or self.environment not in ('test','development') or len(self.grant_secret) < 32:
            raise ApplicationError(404, 'Route not found')
        try:
            accepted=isinstance(credential,str) and len(credential)<=256 and hmac.compare_digest(credential.encode(), self.grant_secret.encode())
        except UnicodeError:accepted=False
        if not accepted:
            raise ApplicationError(401, 'Separate MOCK grant credential required')
        try:
            if (not isinstance(payload,dict) or set(payload) != {'accountId','transactionId','lineIndex','unitIndex','configurationId'}
                    or any(type(payload[key]) is not int or not 0 <= payload[key] <= 2_147_483_647 for key in ('lineIndex','unitIndex'))
                    or not isinstance(payload['configurationId'],str)
                    or re.fullmatch(r'[A-Za-z0-9_.-]{1,80}',payload['configurationId']) is None):
                raise ValueError()
            account, transaction = UUID(payload['accountId']), UUID(payload['transactionId'])
            if not account.int or not transaction.int:raise ValueError()
            digest = hashlib.sha256(json.dumps(payload,sort_keys=True,separators=(',',':')).encode()).digest()
        except (ValueError,TypeError,KeyError,AttributeError):
            raise ApplicationError(422,'Invalid MOCK payment entitlement') from None
        with self.uow() as work:
            prior = work.repository.mock_payment_receipt(transaction,payload['lineIndex'],payload['unitIndex'],digest)
            if prior is not None:return prior
        try:definitions = definition_map(self.definitions.get())
        except (ValueError,TypeError,KeyError):raise ApplicationError(503,'Game content is invalid') from None
        if payload['configurationId'] not in definitions:
            raise ApplicationError(422,'Unknown MOCK weapon configuration')
        with self.uow() as work:
            repo = work.repository
            prior = repo.mock_payment_receipt(transaction,payload['lineIndex'],payload['unitIndex'],digest)
            if prior is not None:return prior
            repo.lock_player(account);repo.ensure(account)
            source = f"mock-payment:{transaction.hex}:{payload['lineIndex']}:{payload['unitIndex']}"
            instance,_ = repo.grant(account,source,payload['configurationId'])
            receipt = {**payload,'status':'APPLIED','instanceId':str(instance),'inventoryRevision':repo.state(account).revision}
            repo.record_mock_payment(transaction,payload['lineIndex'],payload['unitIndex'],account,digest,receipt)
            return receipt
