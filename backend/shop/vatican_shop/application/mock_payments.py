from uuid import UUID
import re

from .access import administrator
from .errors import ApplicationError
from .ports import AuthGateway, UnitOfWorkFactory
from .mock_ports import RewardBundlesGateway, MockProviderGateway, MockEntitlementGateway
from ..domain.mock_payments import (MockPolicy, Scenario, TEST_ENVIRONMENT, canonical, digest, identifier,
    product_id, event_id, parse_event, validate_plan, validate_truth, validate_grant)


def public_product(row):
    return {k: v for k, v in row.items() if k not in ('configurationHash', 'providerId', 'storefrontId')}


def public_order(row, units, replayed=False):
    return {'orderId': str(row['orderId']), 'accountId': str(row['accountId']), 'environment': TEST_ENVIRONMENT,
            'status': row['status'], 'fulfillmentStatus': row['fulfillmentStatus'], 'version': row['version'],
            'transactionId': str(row['transactionId']) if row['transactionId'] else None,
            'providerVersion': row['providerVersion'], 'reviewCode': row['reviewCode'], 'createdAt': row['createdAt'],
            'product': row['snapshot'], 'units': units, 'replayed': replayed}


class MockPayments:
    def __init__(self, uow: UnitOfWorkFactory, auth: AuthGateway, bundles: RewardBundlesGateway, policy: MockPolicy):
        self.uow, self.auth, self.bundles, self.policy = uow, auth, bundles, policy

    def require(self):
        if not self.policy.enabled or self.policy.environment not in ('test', 'development'):
            raise ApplicationError(404, 'Route not found')

    def admin(self, token, credential):
        self.require()
        actor = administrator(self.auth, token)
        if not self.policy.accepts_control(credential):
            raise ApplicationError(401, 'Separate MOCK control credential required')
        return actor

    def player(self, token):
        self.require()
        return self.auth.authenticate(token).account_id

    @staticmethod
    def ids(*values):
        try:
            for value in values:identifier(value)
        except ValueError:
            raise ApplicationError(422, 'Invalid MOCK identifier') from None

    def publish(self, token, credential, content):
        actor = self.admin(token, credential)
        try:
            fields = {'sku', 'version', 'externalSku', 'rewardBundleId', 'priceMinor', 'currencyIso'}
            if (not isinstance(content, dict) or set(content) != fields
                    or not isinstance(content['sku'], str) or re.fullmatch(r'[A-Za-z0-9_.-]{1,128}', content['sku']) is None
                    or not isinstance(content['externalSku'], str) or re.fullmatch(r'[A-Za-z0-9_.-]{1,255}', content['externalSku']) is None
                    or type(content['version']) is not int or not 1 <= content['version'] <= 2_147_483_647
                    or type(content['priceMinor']) is not int or not 0 <= content['priceMinor'] <= 9_223_372_036_854_775_807
                    or not isinstance(content['currencyIso'], str) or re.fullmatch(r'[A-Z]{3}', content['currencyIso']) is None):
                raise ValueError()
            bundle = identifier(content['rewardBundleId'])
        except (ValueError, TypeError, KeyError):
            raise ApplicationError(422, 'Invalid MOCK product') from None
        identity = product_id(content['sku'], content['version'])
        base = {**content, 'rewardBundleId': str(bundle), 'productId': str(identity)}
        with self.uow() as work:
            prior = work.mock.product(identity)
        if prior is not None:
            if any(prior[k] != v for k, v in base.items()):
                raise ApplicationError(409, 'MOCK product version is immutable')
            return {**public_product(prior), 'replayed': True}
        try:
            plan = validate_plan(self.bundles.get(bundle), bundle)
        except (ValueError, TypeError, KeyError):
            raise ApplicationError(422, 'MOCK currently supports published weapon bundles with at most 20 units') from None
        snapshot = {**base, 'rewardSnapshot': plan}
        with self.uow() as work:
            row = work.mock.publish_product(snapshot, digest(snapshot))
            work.mock.lock('actor:' + str(actor))
            if work.mock.command(actor, 'mock.product.publish', identity, digest(base)) is None:
                work.mock.record_command(actor, 'mock.product.publish', identity, digest(base), {'productId': str(identity)})
            return {**public_product(row), 'replayed': False}

    def products(self, token, limit=50, after=None):
        self.player(token)
        if type(limit) is not int or not 1 <= limit <= 100:
            raise ApplicationError(422, 'Invalid MOCK catalog limit')
        if after is not None:self.ids(after)
        with self.uow() as work:
            rows = work.mock.products(limit + 1, after)
        return {'products': [public_product(row) for row in rows[:limit]],
                'nextCursor': rows[limit - 1]['productId'] if len(rows) > limit else None}

    def availability(self, token, credential, identity, enabled, key):
        actor = self.admin(token, credential);self.ids(identity, key)
        if type(enabled) is not bool:raise ApplicationError(422, 'Invalid MOCK availability')
        payload_hash = digest([str(identity), enabled])
        with self.uow() as work:
            work.mock.lock('actor:' + str(actor))
            prior = work.mock.command(actor, 'mock.product.availability', key, payload_hash)
            if prior is not None:return {**prior, 'replayed': True}
            work.mock.availability(identity, enabled)
            result = {'productId': str(identity), 'enabled': enabled, 'replayed': False}
            work.mock.record_command(actor, 'mock.product.availability', key, payload_hash, result)
            return result

    def create(self, token, identity, key):
        account = self.player(token);self.ids(account, identity, key)
        payload_hash = digest({'productId': str(identity)})
        with self.uow() as work:
            repo = work.mock;repo.lock('account:' + str(account))
            prior = repo.by_key(account, key)
            if prior is not None:
                if prior['payloadHash'] != payload_hash:
                    raise ApplicationError(409, 'MOCK order request payload changed')
                return public_order(prior, repo.units(prior['orderId']), True)
            product = repo.product(identity)
            if product is None or not product['enabled']:
                raise ApplicationError(404, 'MOCK product is not available')
            row = repo.create_order(account, key, product, payload_hash)
            return public_order(row, [])

    def get(self, token, identity):
        account = self.player(token);self.ids(identity)
        with self.uow() as work:
            row = work.mock.order(identity)
            if row is None or row['accountId'] != account:
                raise ApplicationError(404, 'MOCK order not found')
            return public_order(row, work.mock.units(identity))

    def inspect(self, token, credential, identity):
        self.admin(token, credential);self.ids(identity)
        with self.uow() as work:
            row = work.mock.order(identity)
            if row is None:raise ApplicationError(404, 'MOCK order not found')
            return public_order(row, work.mock.units(identity))

    def simulate(self, token, credential, identity, key, scenario, unavailable=False):
        actor = self.admin(token, credential);self.ids(identity, key)
        try:
            scenario = Scenario(scenario)
            if type(unavailable) is not bool:raise ValueError()
        except (ValueError, TypeError):
            raise ApplicationError(422, 'Invalid MOCK scenario') from None
        payload_hash = digest([str(identity), scenario, unavailable])
        with self.uow() as work:
            repo = work.mock;repo.lock('actor:' + str(actor))
            prior = repo.command(actor, 'mock.provider.simulate', key, payload_hash)
            if prior is not None:return {**prior, 'replayed': True}
            if repo.order(identity) is None:raise ApplicationError(404, 'MOCK order not found')
            truth = repo.simulate(identity, scenario, unavailable)
            event = {'orderId': str(identity), 'eventId': str(event_id(identity, truth['providerVersion'])),
                     'environment': TEST_ENVIRONMENT, 'providerVersion': truth['providerVersion']}
            result = {'orderId': str(identity), 'scenario': scenario, 'replayed': False,
                      'sdkStatus': 'TIMEOUT' if scenario == Scenario.TIMEOUT else truth['status'],
                      'receipt': None if scenario == Scenario.TIMEOUT else self.policy.receipt(truth),
                      'webhook': {'body': event, 'signature': self.policy.sign_webhook(canonical(event))}}
            repo.record_command(actor, 'mock.provider.simulate', key, payload_hash, result)
            return result

    def webhook(self, body, signature):
        self.require()
        if not isinstance(body,bytes):raise ApplicationError(422,'Invalid MOCK webhook body')
        if len(body) > 16384:raise ApplicationError(413, 'MOCK webhook is too large')
        if not self.policy.check_webhook(body, signature):raise ApplicationError(401, 'Invalid MOCK webhook signature')
        try:event = parse_event(body)
        except ValueError:raise ApplicationError(422, 'Invalid MOCK webhook') from None
        with self.uow() as work:
            accepted = work.mock.signal(UUID(event['orderId']), 'mock.event:' + event['eventId'], digest(event))
        return {'eventId': event['eventId'], 'accepted': True, 'replayed': not accepted}

    def confirm(self, token, identity, receipt):
        account = self.player(token);self.ids(identity)
        with self.uow() as work:
            row = work.mock.order(identity)
            if row is None or row['accountId'] != account:raise ApplicationError(404, 'MOCK order not found')
        try:
            truth = validate_truth(self.policy.parse_receipt(receipt), row['snapshot'], identity, account)
        except (ValueError, TypeError, KeyError):
            raise ApplicationError(422, 'Receipt does not belong to this MOCK order') from None
        with self.uow() as work:
            accepted = work.mock.signal(identity, 'mock.receipt:' + digest(truth).hex(), digest(truth))
            row = work.mock.order(identity)
            return public_order(row, work.mock.units(identity), not accepted)

    def retry(self, token, credential, identity, version, key, reason):
        actor = self.admin(token, credential);self.ids(identity, key)
        if (type(version) is not int or not 1 <= version <= 9_223_372_036_854_775_807
                or not isinstance(reason, str) or not reason.strip() or len(reason) > 500
                or any((ord(c) < 32 and c not in '\n\t') or 0xD800 <= ord(c) <= 0xDFFF for c in reason)):
            raise ApplicationError(422, 'MOCK retry requires a version and reconciliation reason')
        reason = reason.strip();payload_hash = digest([str(identity), version, reason])
        with self.uow() as work:
            repo = work.mock;repo.lock('actor:' + str(actor))
            prior = repo.command(actor, 'mock.order.retry', key, payload_hash)
            if prior is not None:return {**prior, 'replayed': True}
            repo.retry_mock(identity, version)
            result = {'orderId': str(identity), 'version': repo.order(identity)['version'], 'replayed': False, 'reason': reason}
            repo.record_command(actor, 'mock.order.retry', key, payload_hash, result)
            return result


class VerifyMockPayments:
    def __init__(self, uow: UnitOfWorkFactory, provider: MockProviderGateway, policy: MockPolicy):
        self.uow, self.provider, self.policy = uow, provider, policy

    def once(self):
        if not self.policy.enabled:return False
        with self.uow() as work:job = work.mock.claim_verification()
        if job is None:return False
        if job.get('exhausted'):return True
        try:
            truth = validate_truth(self.policy.parse_receipt(self.provider.lookup(job['orderId'])),
                                   job['snapshot'], job['orderId'], job['accountId'])
        except ApplicationError as error:
            with self.uow() as work:work.mock.verification_failure(job, error.status_code < 500)
        except (ValueError, TypeError, KeyError, AttributeError):
            with self.uow() as work:work.mock.verification_failure(job, True)
        else:
            with self.uow() as work:work.mock.apply_verification(job, truth)
        return True


class FulfillMockPayments:
    def __init__(self, uow: UnitOfWorkFactory, inventory: MockEntitlementGateway, policy: MockPolicy):
        self.uow, self.inventory, self.policy = uow, inventory, policy

    def once(self):
        if not self.policy.enabled:return False
        with self.uow() as work:job = work.mock.claim_fulfillment()
        if job is None:return False
        if job.get('exhausted'):return True
        try:
            receipt = validate_grant(job, self.inventory.grant(job))
        except ApplicationError as error:
            with self.uow() as work:work.mock.finish_fulfillment(job, permanent=error.status_code < 500)
        except (ValueError, TypeError, KeyError, AttributeError):
            with self.uow() as work:work.mock.finish_fulfillment(job)
        else:
            with self.uow() as work:work.mock.finish_fulfillment(job, receipt)
        return True
