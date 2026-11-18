from datetime import datetime, timedelta, timezone
import json
import os
from pathlib import Path
import subprocess
import sys
from uuid import UUID, uuid4

import psycopg
import pytest
from sqlalchemy import select, text
from sqlalchemy.exc import DBAPIError, IntegrityError

from vatican_shop.application.errors import ApplicationError
from vatican_shop.application.mock_payments import VerifyMockPayments, FulfillMockPayments
from vatican_shop.domain.mock_payments import canonical, transaction_id
from vatican_shop.persistence.postgres.repositories.mock_payments import MockPaymentsRepository
from vatican_shop.persistence.postgres.tables import (MockOrderState, MockFulfillmentJob, MockProviderPayment,
    PurchaseTransaction, EntitlementGrant, PaymentOrder, ProviderNotification)
from vatican_inventory.persistence.postgres.tables import MockPaymentReceipt
from .test_mock_payments import mock_world, order, simulate, hook, drain, due, count, assert_paid


def test_duplicate_webhook_scenario_and_different_event_ids_keep_one_transaction(mock_world):
    w=mock_world;identity,_=order(w);first=simulate(w,identity)
    duplicate=simulate(w,identity,'DUPLICATE_WEBHOOK')
    assert duplicate['webhook']==first['webhook']
    for _ in range(20):hook(w,duplicate)
    drain(w);assert_paid(w,identity)
    another=simulate(w,identity);assert another['webhook']['body']['eventId']!=first['webhook']['body']['eventId']
    hook(w,another);drain(w);assert_paid(w,identity)


def test_same_signed_webhook_with_different_json_whitespace_is_semantically_idempotent(mock_world):
    w=mock_world;identity,_=order(w);event=simulate(w,identity)['webhook']['body']
    a=canonical(event);b=json.dumps(event,indent=4).encode()
    assert not w.api.webhook(a,w.policy.sign_webhook(a))['replayed']
    assert w.api.webhook(b,w.policy.sign_webhook(b))['replayed']
    drain(w);assert_paid(w,identity)


def test_refund_racing_an_inflight_grant_keeps_the_actual_grant_receipt_for_review(mock_world):
    w=mock_world;identity,_=order(w);simulate(w,identity);assert w.verify.once()
    with w.uow() as work:claim=work.mock.claim_fulfillment()
    receipt=w.delivery.grant(claim)
    hook(w,simulate(w,identity,'REFUND'));assert w.verify.once()
    with w.uow() as work:assert work.mock.finish_fulfillment(claim,receipt)
    result=w.api.get('user',identity)
    assert result['status']=='REFUNDED' and result['fulfillmentStatus']=='REVIEW'
    assert result['units'][0]['receipt']['instanceId']==receipt['instanceId']
    assert count(w.db,EntitlementGrant)==1 and count(w.inventory,MockPaymentReceipt)==1
    assert not w.fulfill.once()


def test_verification_transaction_rollback_recovers_without_partial_transaction_or_jobs(mock_world,monkeypatch):
    w=mock_world;identity,_=order(w);simulate(w,identity)
    original=MockPaymentsRepository.apply_verification
    def fail(repo,*args):
        original(repo,*args);repo.session.flush()
        raise RuntimeError('Injected failure before verification COMMIT')
    with monkeypatch.context() as patch:
        patch.setattr(MockPaymentsRepository,'apply_verification',fail)
        with pytest.raises(RuntimeError):w.verify.once()
    assert count(w.db,PurchaseTransaction)==count(w.db,MockFulfillmentJob)==0
    due(w,identity,verify=True);drain(w);assert_paid(w,identity)


def test_entitlement_record_failure_rolls_back_merchant_receipt_but_replays_applied_inventory(mock_world):
    w=mock_world;identity,_=order(w);simulate(w,identity);assert w.verify.once()
    with w.db.engine.begin() as connection:
        connection.execute(text("ALTER TABLE entitlement_grants ADD CONSTRAINT injected_mock_failure CHECK (amount < 0)"))
    try:
        with pytest.raises(IntegrityError):w.fulfill.once()
        assert count(w.inventory,MockPaymentReceipt)==1 and count(w.db,EntitlementGrant)==0
    finally:
        with w.db.engine.begin() as connection:connection.execute(text('ALTER TABLE entitlement_grants DROP CONSTRAINT injected_mock_failure'))
    due(w,identity);drain(w);assert_paid(w,identity)


def test_disconnected_order_transaction_has_no_orphan_provider_payment(mock_world,monkeypatch):
    w=mock_world;product,_=w.publish();key=uuid4();original=MockPaymentsRepository.create_order
    def interrupt(repo,*args):
        result=original(repo,*args)
        pid=repo.session.connection().exec_driver_sql('SELECT pg_backend_pid()').scalar_one()
        with psycopg.connect(w.url) as control:control.execute('SELECT pg_terminate_backend(%s)',(pid,))
        return result
    with monkeypatch.context() as patch:
        patch.setattr(MockPaymentsRepository,'create_order',interrupt)
        with pytest.raises(DBAPIError):order(w,product,key)
    assert count(w.db,PaymentOrder)==count(w.db,MockProviderPayment)==0
    identity,_=order(w,product,key);simulate(w,identity);drain(w);assert_paid(w,identity)


def test_expired_twentieth_verification_lease_never_sends_a_twenty_first_lookup(mock_world):
    w=mock_world;identity,_=order(w);simulate(w,identity)
    with w.db.transaction() as session:
        state=session.get(MockOrderState,identity);state.attempts=20;state.lease_token=uuid4()
        state.available_at=datetime.now(timezone.utc)-timedelta(seconds=1)
    class Forbidden:
        def lookup(self,*args):pytest.fail('Unexpected twenty-first provider call')
    assert VerifyMockPayments(w.uow,Forbidden(),w.policy).once()
    result=w.api.get('user',identity);assert result['reviewCode']=='VerificationExhausted'
    w.api.retry('admin',w.policy.control_secret,identity,result['version'],uuid4(),'Provider state checked')
    drain(w);assert_paid(w,identity)


def test_expired_twentieth_delivery_lease_requires_review_without_another_grant(mock_world):
    w=mock_world;identity,_=order(w);simulate(w,identity);w.verify.once()
    with w.db.transaction() as session:
        job=session.get(MockFulfillmentJob,(identity,0,0));job.attempts=20;job.lease_token=uuid4()
        job.available_at=datetime.now(timezone.utc)-timedelta(seconds=1)
    assert w.fulfill.once() and w.delivery.calls==0
    result=w.api.get('user',identity);assert result['fulfillmentStatus']=='REVIEW'
    assert not w.fulfill.once()


def test_forged_or_misbound_provider_lookup_is_not_accepted_as_verified(mock_world):
    w=mock_world;identity,_=order(w);simulate(w,identity)
    class FakeProvider:
        def lookup(self,source):
            truth=w.policy.parse_receipt(w.provider.lookup(source))
            return w.policy.receipt({**truth,'externalSku':'not-the-paid-sku'})
    assert VerifyMockPayments(w.uow,FakeProvider(),w.policy).once()
    result=w.api.get('user',identity);assert result['reviewCode']=='ProviderVerificationRejected'
    assert count(w.db,PurchaseTransaction)==count(w.inventory,MockPaymentReceipt)==0


CHILD=r'''
import json,os
from vatican_shop.domain.mock_payments import MockPolicy,grant_payload
from vatican_shop.persistence.postgres.database import Database
from vatican_shop.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory
from vatican_shop.application.mock_payments import MockPayments,FulfillMockPayments
from vatican_inventory.persistence.postgres.database import Database as InventoryDatabase
from vatican_inventory.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as InventoryUow
from vatican_inventory.application.mock_payments import MockPaymentGrants
p=MockPolicy(True,'test','c'*32,'w'*32,'r'*32,'i'*32)
uow=PostgresUnitOfWorkFactory(Database(os.environ['MOCK_SHOP_TEST_URL']))
if os.environ['MOCK_CRASH_POINT']=='webhook':
    signal=json.loads(os.environ['MOCK_TEST_WEBHOOK'])
    from vatican_shop.domain.mock_payments import canonical
    MockPayments(uow,None,None,p).webhook(canonical(signal['body']),signal['signature'])
    os._exit(92)
class Definitions:
    def get(self):return [{'Id':key,'ExperiencePerLevel':100,'MaximumLevel':10} for key in ('sword.common','sword.rare')]
inventory=MockPaymentGrants(InventoryUow(InventoryDatabase(os.environ['MOCK_INV_TEST_URL'])),Definitions(),True,'test',p.grant_secret)
class Gateway:
    def grant(self,job):
        inventory.grant(grant_payload(job),p.grant_secret)
        os._exit(91)
FulfillMockPayments(uow,Gateway(),p).once()
raise RuntimeError('Crash point not reached')
'''


@pytest.mark.parametrize('point',['webhook','grant'])
def test_hard_process_crash_after_committed_effect_restarts_idempotently(mock_world,point):
    w=mock_world;identity,_=order(w);signal=simulate(w,identity)
    if point=='grant':assert w.verify.once()
    backend=Path(__file__).resolve().parents[2]
    env={**os.environ,'MOCK_SHOP_TEST_URL':w.url,'MOCK_INV_TEST_URL':w.inv_url,'MOCK_CRASH_POINT':point,
         'MOCK_TEST_WEBHOOK':json.dumps(signal['webhook']),
         'PYTHONPATH':os.pathsep.join(str(backend/name) for name in ('shop','inventory'))}
    child=subprocess.run([sys.executable,'-c',CHILD],env=env,capture_output=True,text=True,timeout=60)
    assert child.returncode==(92 if point=='webhook' else 91),child.stderr
    if point=='webhook':assert hook(w,signal)['replayed']
    else:
        assert count(w.inventory,MockPaymentReceipt)==1
        due(w,identity)
    w.verify=VerifyMockPayments(w.uow,w.provider,w.policy)
    w.fulfill=FulfillMockPayments(w.uow,w.delivery,w.policy)
    drain(w);assert_paid(w,identity)
