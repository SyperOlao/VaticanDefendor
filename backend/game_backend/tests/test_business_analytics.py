from concurrent.futures import ThreadPoolExecutor
from uuid import UUID, uuid4
import pytest
from sqlalchemy import func, select, text
from sqlalchemy.exc import IntegrityError
from vatican_backend.application.errors import ApplicationError
from vatican_backend.application.messaging import DeliverRewards
from vatican_backend.domain.analytics import GIFT_ANALYTICS_EVENT, CASE_ANALYTICS_EVENT, analytics_message_id, expected_receipt
from vatican_backend.persistence.postgres.tables import OutboxMessage, LevelRewardGrant, PlayerLevelRewardClaim, CaseOpening
from vatican_analytics.application.errors import ApplicationError as AnalyticsError
from .test_level_rewards_lifecycle import gift_game, claim, get, drain, ready_again, balances, Delivery
from .test_progression_lifecycle import xp_game
from .test_cases import case_game, open_case, deliver, get as get_case, items, CaseDelivery


class AnalyticsDown(Delivery):
    def deliver(self,message):
        if message['destination']=='analytics':raise ApplicationError(503,'Analytics unavailable')
        return super().deliver(message)


class CaseAnalyticsDown(CaseDelivery):
    def deliver(self,message):
        if message['destination']=='analytics':raise ApplicationError(503,'Analytics unavailable')
        return super().deliver(message)


def test_gift_event_waits_for_all_lines_and_survives_analytics_outage_without_reissuing(gift_game):
    w=gift_game
    assert get(w)['analyticsStatus']=='SKIPPED'
    assert claim(w)['analyticsStatus']=='SKIPPED'
    drain(w,Delivery(w,down='inventory'))
    assert get(w)['status']=='PENDING' and get(w)['analyticsStatus']=='SKIPPED'
    with w['game'].transaction() as session:assert session.get(OutboxMessage,analytics_message_id(w['claim'],GIFT_ANALYTICS_EVENT)) is None
    ready_again(w);drain(w,AnalyticsDown(w))
    pending=get(w)
    assert pending['status']=='CLAIMED' and pending['analyticsStatus']=='PENDING'
    money,weapons=balances(w),w['inventory'].get(w['account'])
    assert claim(w)['replayed'] and claim(w)['lines']==pending['lines']
    ready_again(w);drain(w)
    final=get(w)
    assert final['status']=='CLAIMED' and final['analyticsStatus']=='COMPLETED'
    assert balances(w)==money and w['inventory'].get(w['account'])==weapons
    assert len(w['analytics'].projection.tables['fact_level_gift_claims'])==1
    with w['game'].transaction() as session:payload=session.get(OutboxMessage,analytics_message_id(w['claim'],GIFT_ANALYTICS_EVENT)).payload
    assert final['analytics']['receipt']==expected_receipt(payload,GIFT_ANALYTICS_EVENT)
    assert payload['claimedAt']==final['claimedAt']


@pytest.mark.parametrize('mode',['INVENTORY','WALLET'])
def test_case_event_waits_for_all_rewards_and_analytics_retry_never_changes_payment_or_pity(case_game,mode):
    w=case_game;first=open_case(w,mode=mode)
    assert first['analyticsStatus']=='SKIPPED'
    service=DeliverRewards(w['use_case'].uow,CaseDelivery(w),w['cases']);assert service.once()
    deliver(w,down='inventory')
    partial=get_case(w,first['openingId'])
    assert partial['status']=='PENDING' and partial['analyticsStatus']=='SKIPPED'
    ready_again(w)
    service=DeliverRewards(w['use_case'].uow,CaseAnalyticsDown(w),w['cases'])
    while service.once():pass
    pending=get_case(w,first['openingId'])
    assert pending['status']=='COMPLETED' and pending['analyticsStatus']=='PENDING'
    money,stack=balances(w),items(w);result=pending['result']
    ready_again(w);deliver(w)
    final=get_case(w,first['openingId'])
    assert final['analyticsStatus']=='COMPLETED' and final['result']==result
    assert balances(w)==money and items(w)==stack
    fact=next(iter(w['analytics'].projection.tables['fact_case_settlements'].values()))
    assert fact['opening_id']==UUID(first['openingId']) and fact['payment_mode']==mode
    assert fact['pity_before']==result['pityBefore'] and fact['pity_after']==result['pityAfter']
    assert final['analytics']['receipt']['eventIds'] and 'random_seed' not in fact


def test_gift_local_completion_and_parallel_confirmation_create_one_immutable_event(gift_game):
    w=gift_game;claim(w)
    with ThreadPoolExecutor(max_workers=4) as pool:
        for _ in range(8):list(pool.map(lambda _:DeliverRewards(w['use_case'].uow,Delivery(w)).once(),range(4)))
    assert get(w)['status']=='CLAIMED'
    identity=analytics_message_id(w['claim'],GIFT_ANALYTICS_EVENT)
    with w['game'].transaction() as session:
        row=session.get(OutboxMessage,identity);saved=row.payload
        assert session.scalar(select(func.count()).select_from(OutboxMessage).where(OutboxMessage.id==identity))==1
    with ThreadPoolExecutor(max_workers=4) as pool:list(pool.map(lambda _:claim(w),range(4)))
    with w['game'].transaction() as session:assert session.get(OutboxMessage,identity).payload==saved


def test_failed_gift_completion_rolls_back_analytics_and_retries_remote_receipt(gift_game):
    w=gift_game;claim(w)
    with w['game'].engine.begin() as conn:
        conn.execute(text("ALTER TABLE player_level_reward_claims ADD CONSTRAINT injected_gift_finish CHECK (status <> 'CLAIMED')"))
    try:
        with pytest.raises(IntegrityError):drain(w)
        assert get(w)['analyticsStatus']=='SKIPPED'
        with w['game'].transaction() as session:assert session.get(OutboxMessage,analytics_message_id(w['claim'],GIFT_ANALYTICS_EVENT)) is None
    finally:
        with w['game'].engine.begin() as conn:conn.execute(text('ALTER TABLE player_level_reward_claims DROP CONSTRAINT injected_gift_finish'))
    money=balances(w);ready_again(w);drain(w)
    assert get(w)['analyticsStatus']=='COMPLETED' and balances(w)==money


def test_case_rejection_never_emits_opened_event(case_game):
    w=case_game
    with w['inventory_db'].transaction() as session:
        from vatican_inventory.persistence.postgres.tables import InventoryItem
        session.get(InventoryItem,(w['account'],'case:level-case')).quantity=0
    response=open_case(w);deliver(w)
    final=get_case(w,response['openingId'])
    assert final['status']=='REJECTED' and final['analyticsStatus']=='SKIPPED'
    with w['game'].transaction() as session:assert session.get(OutboxMessage,analytics_message_id(UUID(response['openingId']),CASE_ANALYTICS_EVENT)) is None


class BusinessFault:
    def __init__(self,w,base,mode):self.w,self.base,self.mode=w,base,mode
    def deliver(self,message):
        if message['destination']!='analytics':return self.base.deliver(message)
        if self.mode=='down':raise ApplicationError(503,'Analytics unavailable')
        target=self.w['analytics'].api if self.mode=='async' else self.w['analytics']
        response=target.receive(message['id'],message['event_name'],message['schema_version'],message['payload'])
        if response['status']=='PENDING':raise ApplicationError(503,'Projection pending')
        if self.mode=='lost':raise ApplicationError(503,'Analytics acknowledgement lost after projection')
        result=response['receipt']
        return {**result,'payloadHash':'0'*64} if self.mode=='wrong' else result


def business(request,kind):
    w=request.getfixturevalue('gift_game' if kind=='gift' else 'case_game')
    if kind=='gift':
        claim(w);identity=w['claim'];event=GIFT_ANALYTICS_EVENT;base=Delivery(w);status=lambda:get(w)
    else:
        first=open_case(w);identity=UUID(first['openingId']);event=CASE_ANALYTICS_EVENT;base=CaseDelivery(w)
        status=lambda:get_case(w,str(identity))
    return w,identity,event,base,status


def drain_fault(w,base,mode):
    service=DeliverRewards(w['use_case'].uow,BusinessFault(w,base,mode),w['client'].app.state.use_cases.cases)
    for _ in range(40):
        if not service.once():return
    raise AssertionError('Delivery did not quiesce')


@pytest.mark.parametrize('kind',['gift','case'])
@pytest.mark.parametrize('mode',['lost','wrong'])
def test_business_lost_or_mismatched_receipt_replays_without_reissuing_or_reprojecting(request,kind,mode):
    w,identity,event,base,status=business(request,kind)
    drain_fault(w,base,mode)
    before=status();assert before['analyticsStatus']=='PENDING'
    calls=w['analytics'].projection.calls;money=balances(w);weapons=w['inventory'].get(w['account'])
    ready_again(w);drain_fault(w,base,'normal')
    after=status()
    assert after['analyticsStatus']=='COMPLETED' and after['status']==before['status']
    assert balances(w)==money and w['inventory'].get(w['account'])==weapons
    assert w['analytics'].projection.calls==calls
    assert after['lines']==before['lines']


@pytest.mark.parametrize('kind',['gift','case'])
def test_business_analytics_review_and_manual_replay_preserve_completed_operation(request,kind):
    w,identity,event,base,status=business(request,kind)
    drain_fault(w,base,'down')
    message_id=analytics_message_id(identity,event)
    with w['game'].transaction() as session:
        row=session.get(OutboxMessage,message_id);row.attempts=19
    ready_again(w);drain_fault(w,base,'down')
    saved=status();assert saved['analyticsStatus']=='REVIEW'
    money=balances(w);weapons=w['inventory'].get(w['account'])
    assert w['client'].app.state.use_cases.rewards.requeue(message_id)
    drain_fault(w,base,'normal')
    assert status()['analyticsStatus']=='COMPLETED' and status()['lines']==saved['lines']
    assert balances(w)==money and w['inventory'].get(w['account'])==weapons


@pytest.mark.parametrize('kind',['gift','case'])
def test_business_inbox_pending_waits_for_projection_before_game_acknowledgement(request,kind):
    w,identity,event,base,status=business(request,kind)
    drain_fault(w,base,'async')
    assert status()['analyticsStatus']=='PENDING'
    assert w['analytics'].api.get(identity,event)['status']=='PENDING'
    assert w['analytics'].worker.once()
    ready_again(w);drain_fault(w,base,'async')
    assert status()['analyticsStatus']=='COMPLETED'


@pytest.mark.parametrize('kind',['gift','case'])
def test_business_analytics_confirmation_commit_failure_keeps_event_and_business_receipts(request,kind):
    w,identity,event,base,status=business(request,kind)
    message_id=analytics_message_id(identity,event)
    with w['game'].engine.begin() as connection:
        connection.exec_driver_sql(f"ALTER TABLE outbox_messages ADD CONSTRAINT injected_business_analytics_ack CHECK (id <> '{message_id}'::uuid OR published_at IS NULL)")
    try:
        with pytest.raises(IntegrityError):drain_fault(w,base,'normal')
        saved=status();assert saved['analyticsStatus']=='PENDING'
        calls=w['analytics'].projection.calls
    finally:
        with w['game'].engine.begin() as connection:connection.execute(text('ALTER TABLE outbox_messages DROP CONSTRAINT injected_business_analytics_ack'))
    money=balances(w);ready_again(w);drain_fault(w,base,'normal')
    assert status()['analyticsStatus']=='COMPLETED' and status()['lines']==saved['lines']
    assert balances(w)==money and w['analytics'].projection.calls==calls


def test_case_with_only_local_reward_emits_event_in_funding_settlement_transaction(case_game):
    from dataclasses import replace
    from .test_cases import reward,line
    from vatican_backend.application.cases import PublishCase
    from vatican_backend.application.level_rewards import PublishRewardBundle
    from vatican_backend.domain.cases import Drop
    w=case_game;plan=reward(uuid4(),[line(0,'ENERGY','ENERGY',3)])
    PublishRewardBundle(w['use_case'].uow).execute('case-local-only',1,plan)
    rules=replace(w['case_rules'],id=uuid4(),ruleset_id='test-energy-v1',drops=(Drop(0,'RARE',1,plan),))
    PublishCase(w['use_case'].uow).execute(rules);w['case']=rules.id
    first=open_case(w)
    assert DeliverRewards(w['use_case'].uow,CaseDelivery(w),w['cases']).once()
    completed=get_case(w,first['openingId'])
    assert completed['status']=='COMPLETED' and completed['analyticsStatus']=='PENDING'
    assert all(row['messageId'] is None for row in completed['lines'])
    with w['game'].transaction() as session:
        assert session.get(OutboxMessage,analytics_message_id(UUID(first['openingId']),CASE_ANALYTICS_EVENT)) is not None
