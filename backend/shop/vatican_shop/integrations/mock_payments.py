import httpx
from ..application.errors import ApplicationError
from ..application.ports import UnitOfWorkFactory
from ..domain.mock_payments import MockPolicy, grant_payload


class RewardBundlesHttpGateway:
    def __init__(self, client, settings):
        self.client,self.settings=client,settings

    def get(self, bundle_id):
        try:
            response=self.client.get(self.settings.game_url.rstrip('/')+'/v1/reward-bundles/'+str(bundle_id))
            if response.status_code==404:raise ApplicationError(422,'Published reward bundle not found')
            response.raise_for_status()
            return response.json()
        except (httpx.HTTPError,ValueError):
            raise ApplicationError(503,'Reward bundle service is unavailable') from None


class LocalMockProvider:
    """The embedded TEST provider ledger is queried independently of merchant transactions."""
    def __init__(self,uow: UnitOfWorkFactory,policy: MockPolicy):
        self.uow,self.policy=uow,policy

    def lookup(self,order_id):
        if not self.policy.enabled:raise ApplicationError(404,'Route not found')
        with self.uow() as work:
            truth,unavailable=work.mock.provider(order_id)
        if unavailable:raise ApplicationError(503,'MOCK provider is unavailable')
        return self.policy.receipt(truth)


class MockEntitlementsHttpGateway:
    def __init__(self,client,settings):
        self.client,self.settings=client,settings

    def grant(self,job):
        try:
            response=self.client.post(self.settings.inventory_url.rstrip('/')+'/internal/mock-payment-grants',
                json=grant_payload(job),headers={'X-Service-Secret':self.settings.service_secret,
                                               'X-Mock-Grant-Key':self.settings.mock_grant_secret})
        except httpx.HTTPError:
            raise ApplicationError(503,'MOCK entitlement delivery is unavailable') from None
        if response.status_code!=200:
            raise ApplicationError(response.status_code if response.status_code in (409,422) else 503,
                                   'MOCK entitlement delivery was not confirmed')
        try:return response.json()
        except ValueError:raise ApplicationError(503,'Invalid MOCK entitlement acknowledgement') from None
