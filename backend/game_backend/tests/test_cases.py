from concurrent.futures import ThreadPoolExecutor
from copy import deepcopy
from datetime import datetime, timedelta, timezone
from uuid import UUID, uuid4

import pytest
from sqlalchemy import func, select, text
from sqlalchemy.exc import IntegrityError
from vatican_backend.application.cases import PublishCase
from vatican_backend.application.errors import ApplicationError
from vatican_backend.application.level_rewards import PublishRewardBundle
from vatican_backend.application.messaging import DeliverRewards
from vatican_backend.domain.cases import CaseRules, CASE_REWARD, CASE_CONSUME, CASE_PAYMENT, validate_case_receipt
from vatican_backend.domain.level_rewards import RewardPlan, LEVEL_REWARD_EVENT
from vatican_backend.persistence.postgres.tables import (CaseDefinition, CaseDropEntry, CaseOpening, CaseRewardGrant,
    CaseType, OutboxMessage, PlayerCasePity, PlayerEffect, RewardBundleItem)
from vatican_wallet.application.errors import ApplicationError as WalletError
from vatican_inventory.application.errors import ApplicationError as InventoryError
from .test_level_rewards_lifecycle import gift_game, claim, drain, ready_again, balances
from .test_progression_lifecycle import xp_game


def reward(bundle, items):
    return RewardPlan.parse({'schemaVersion':1,'bundleId':str(bundle),'items':items})


def line(index, kind, ref, quantity, **extra):
    return {'itemIndex':index,'rewardType':kind,'rewardRef':ref,'quantity':quantity,**extra}


@pytest.fixture
def case_game(gift_game):
    w = gift_game
    claim(w); drain(w)
    with w['game'].transaction() as session:
        session.add(CaseType(code='bonus-case'))
    common = reward(uuid4(),[line(0,'CURRENCY','GEMS',5),line(1,'CURRENCY','PRESTIGE',12),
        line(2,'WEAPON','sword.common',2),line(3,'ITEM','repair-kit',2),line(4,'CASE','bonus-case',1),
        line(5,'ENERGY','ENERGY',3),line(6,'EFFECT','ENERGY_REGEN_RATE_BONUS',1,
             effect={'modifierValue':'0.500000','durationSeconds':60})])
    rare = reward(uuid4(),[line(0,'CURRENCY','GEMS',50)])
    publish = PublishRewardBundle(w['use_case'].uow)
    publish.execute('case-common',1,common)
    publish.execute('case-rare',1,rare)
    with w['game'].transaction() as session:
        ruleset = session.get(CaseType,'level-case')
        assert ruleset is not None
    rules = CaseRules.parse({'schemaVersion':1,'caseId':str(uuid4()),'code':'level-case',
        'rulesetId':w['use_case'].get(w['account'],w['claim'])['rulesetId'],'priceCurrency':'PRESTIGE','price':25,
        'pityThreshold':3,'guaranteedRarity':'RARE','drops':[
            {'entryIndex':0,'rarity':'COMMON','weight':9,'reward':common.snapshot()},
            {'entryIndex':1,'rarity':'RARE','weight':1,'reward':rare.snapshot()}]})
    PublishCase(w['use_case'].uow).execute(rules)
    w.update(cases=w['client'].app.state.use_cases.cases,case=rules.id,case_rules=rules,
             money_before=balances(w),owned_before=len(w['inventory'].get(w['account'])['Weapons']))
    w['cases'].randbelow = lambda upper: 0
    return w


class CaseDelivery:
    def __init__(self, world, down=None, lost=None, reject=None):
        self.w,self.down,self.lost,self.reject = world,down,lost,reject

    def deliver(self, message):
        assert getattr(self.w['active'],'game',0)==0
        if message['destination']==self.down:
            raise ApplicationError(503,'Participant unavailable')
        if message['event_name']==CASE_REWARD and message['destination']==self.reject:
            raise ApplicationError(422,'Participant rejected reward')
        participant = self.w[message['destination']]
        try:
            reply = participant.receive(message['id'],message['event_name'],message['schema_version'],message['payload'])
        except (WalletError,InventoryError) as error:
            raise ApplicationError(error.status_code,'Participant rejected operation') from None
        if message['event_name']==self.lost or (self.lost=='reward' and message['event_name']==CASE_REWARD):
            raise ApplicationError(503,'Acknowledgement lost')
        return reply.get('receipt')


def worker(w, **options):
    return DeliverRewards(w['use_case'].uow,CaseDelivery(w,**options),w['cases'])


def deliver(w, **options):
    instance = worker(w,**options)
    for _ in range(40):
        if not instance.once():
            return
    raise AssertionError('Worker did not quiesce')


def open_case(w, key=None, mode='INVENTORY'):
    return w['cases'].open(w['account'],w['case'],key or uuid4(),mode)


def get(w, identity):
    return w['cases'].get(w['account'],UUID(identity))


def items(w):
    return {r['itemId']:r['quantity'] for r in w['inventory'].items(w['account'])['items']}


def give_cases(w, quantity):
    payload = {'accountId':str(w['account']),'claimId':str(uuid4()),**line(0,'CASE','level-case',quantity)}
    w['inventory'].receive(uuid4(),LEVEL_REWARD_EVENT,1,payload)


def test_owned_case_consumption_draw_and_mixed_rewards_are_idempotent(case_game):
    w=case_game
    key=uuid4()
    first=open_case(w,key)
    assert first['status']=='FUNDING' and first['result'] is None and first['lines']==[]
    assert open_case(w,key)['openingId']==first['openingId']
    deliver(w)
    final=get(w,first['openingId'])
    assert final['status']=='COMPLETED' and final['result']['rarity']=='COMMON'
    assert final['result']['pityAfter']==1 and not final['result']['guaranteedTriggered']
    assert items(w)['case:level-case']==0 and items(w)['case:bonus-case']==1
    assert balances(w)=={'PRESTIGE':w['money_before']['PRESTIGE']+12,'GEMS':w['money_before']['GEMS']+5}
    assert len(w['inventory'].get(w['account'])['Weapons'])==w['owned_before']+2
    assert final['lines'][6]['receipt']['effectId']
    assert all(r['status']=='COMPLETED' for r in final['lines'])
    replay=open_case(w,key)
    assert replay['replayed'] and replay['lines']==final['lines'] and replay['result']==final['result']
    rejected=open_case(w)
    deliver(w)
    assert get(w,rejected['openingId'])['status']=='REJECTED'
    with w['game'].transaction() as session:
        assert session.get(PlayerCasePity,(w['account'],'level-case')).rolls_since_guarantee==1
        assert session.scalar(select(func.count()).select_from(PlayerEffect))==1


def test_concurrent_request_and_pending_new_key_consume_one_case(case_game):
    w=case_game
    key=uuid4()
    with ThreadPoolExecutor(max_workers=6) as workers:
        replies=list(workers.map(lambda _:open_case(w,key),range(6)))
    assert len({r['openingId'] for r in replies})==1
    assert sum(not r['replayed'] for r in replies)==1
    with pytest.raises(ApplicationError,match='still pending'):
        open_case(w)
    with pytest.raises(ApplicationError,match='payload changed'):
        open_case(w,key,'WALLET')
    deliver(w)
    assert items(w)['case:level-case']==0


@pytest.mark.parametrize('down',['wallet','inventory'])
def test_partial_service_outage_resumes_without_changing_result_or_local_effect(case_game,down):
    w=case_game
    first=open_case(w)
    assert worker(w).once()  # Confirm case consumption and atomically pin the draw.
    result=get(w,first['openingId'])['result']
    deliver(w,down=down)
    partial=get(w,first['openingId'])
    assert partial['status']=='PENDING' and partial['deliveryStatus']=='PARTIAL'
    before_money,before_items=balances(w),items(w)
    effect=partial['lines'][6]
    ready_again(w);deliver(w)
    final=get(w,first['openingId'])
    assert final['status']=='COMPLETED' and final['result']==result and final['lines'][6]==effect
    if down=='inventory':
        assert balances(w)==before_money
    else:
        assert items(w)==before_items
    assert len(w['inventory'].get(w['account'])['Weapons'])==w['owned_before']+2


@pytest.mark.parametrize('lost',[CASE_CONSUME,CASE_PAYMENT,'reward'])
def test_lost_acknowledgements_never_repeat_debit_draw_or_grant(case_game,lost):
    w=case_game
    mode='WALLET' if lost==CASE_PAYMENT else 'INVENTORY'
    first=open_case(w,mode=mode)
    deliver(w,lost=lost)
    money,stack=balances(w),items(w)
    prior=get(w,first['openingId'])
    if lost!='reward':
        assert prior['result'] is None and prior['status']=='FUNDING'
    ready_again(w);deliver(w)
    final=get(w,first['openingId'])
    assert final['status']=='COMPLETED'
    assert items(w)['case:level-case']==(1 if mode=='WALLET' else 0)
    expected=w['money_before']['PRESTIGE']+12-(25 if mode=='WALLET' else 0)
    assert balances(w)['PRESTIGE']==expected
    if lost=='reward':
        assert balances(w)==money and items(w)==stack and final['result']==prior['result']


def test_permanent_reward_failure_review_and_redrive_preserve_completed_lines(case_game):
    w=case_game
    first=open_case(w)
    deliver(w,reject='inventory')
    partial=get(w,first['openingId'])
    assert partial['status']=='REVIEW'
    money=balances(w)
    for row in partial['lines']:
        if row['status']=='FAILED':
            assert w['cases'].uow
            assert w['client'].app.state.use_cases.rewards.requeue(UUID(row['messageId']))
    deliver(w)
    final=get(w,first['openingId'])
    assert final['status']=='COMPLETED' and balances(w)==money
    assert final['lines'][6]==partial['lines'][6]


def test_pity_guarantees_exact_rarity_and_survives_rule_version_changes(case_game):
    from dataclasses import replace
    w=case_game
    give_cases(w,3)
    opened=[]
    for _ in range(2):
        response=open_case(w);deliver(w);opened.append(get(w,response['openingId']))
    updated=replace(w['case_rules'],id=uuid4(),ruleset_id='test-energy-v1')
    PublishCase(w['cases'].uow).execute(updated)
    w['case']=updated.id
    third=open_case(w);deliver(w)
    guaranteed=get(w,third['openingId'])
    assert [r['result']['pityAfter'] for r in opened]==[1,2]
    assert guaranteed['result']['guaranteedTriggered'] and guaranteed['result']['rarity']=='RARE'
    assert guaranteed['result']['pityAfter']==0
    w['cases'].randbelow=lambda total:total-1  # Natural rare also resets without forced guarantee.
    natural=open_case(w);deliver(w)
    assert not get(w,natural['openingId'])['result']['guaranteedTriggered']
    assert get(w,natural['openingId'])['result']['pityAfter']==0


def test_insufficient_wallet_funds_are_durable_and_do_not_consume_owned_case(case_game):
    w=case_game
    with w['wallet_db'].transaction() as session:
        from vatican_wallet.persistence.postgres.tables import CurrencyBalance
        session.get(CurrencyBalance,(w['account'],'PRESTIGE')).balance=1
    key=uuid4();first=open_case(w,key,'WALLET');deliver(w)
    final=get(w,first['openingId'])
    assert final['status']=='REJECTED' and final['paymentReceipt']['code']=='InsufficientFunds'
    assert final['result'] is None and final['lines']==[] and items(w)['case:level-case']==1
    give_cases(w,1)
    assert open_case(w,key,'WALLET')['status']=='REJECTED'
    with w['game'].transaction() as session:
        assert session.get(PlayerCasePity,(w['account'],'level-case')) is None


def test_snapshot_is_frozen_before_consumption_and_publication_is_immutable(case_game):
    from dataclasses import replace
    w=case_game
    PublishCase(w['cases'].uow).execute(w['case_rules'])
    with pytest.raises(ApplicationError,match='immutable'):
        PublishCase(w['cases'].uow).execute(replace(w['case_rules'],price=999))
    first=open_case(w)
    with w['game'].transaction() as session:
        session.get(CaseDefinition,w['case']).pity_threshold=1
        for row in session.scalars(select(RewardBundleItem).where(RewardBundleItem.bundle_id==w['case_rules'].drops[0].plan.bundle_id)):
            if row.reward_type=='CURRENCY':row.quantity=999
    deliver(w)
    final=get(w,first['openingId'])
    assert final['result']['rarity']=='COMMON' and final['result']['pityAfter']==1
    assert balances(w)['GEMS']==w['money_before']['GEMS']+5


def test_funding_commit_failure_retries_remote_consumption_without_reroll_or_double_debit(case_game):
    w=case_game
    first=open_case(w)
    with w['game'].engine.begin() as conn:
        conn.execute(text("ALTER TABLE case_reward_grants ADD CONSTRAINT injected_draw_failure CHECK (quantity < 0)"))
    try:
        with pytest.raises(IntegrityError):worker(w).once()
        assert items(w)['case:level-case']==0
        assert get(w,first['openingId'])['result'] is None
        with w['game'].transaction() as session:
            assert session.get(PlayerCasePity,(w['account'],'level-case')) is None
            assert session.scalar(select(func.count()).select_from(PlayerEffect))==0
    finally:
        with w['game'].engine.begin() as conn:conn.execute(text('ALTER TABLE case_reward_grants DROP CONSTRAINT injected_draw_failure'))
    ready_again(w);deliver(w)
    assert get(w,first['openingId'])['status']=='COMPLETED'
    assert items(w)['case:level-case']==0


def test_funding_timeout_review_and_requeue_keep_original_debit_source(case_game):
    w=case_game
    first=open_case(w)
    with w['game'].transaction() as session:
        row=session.get(OutboxMessage,UUID(first['paymentMessageId']));row.attempts=19
    deliver(w,down='inventory')
    assert get(w,first['openingId'])['status']=='REVIEW'
    assert w['client'].app.state.use_cases.rewards.requeue(UUID(first['paymentMessageId']))
    deliver(w)
    assert get(w,first['openingId'])['status']=='COMPLETED'


@pytest.mark.parametrize('mode',['INVENTORY','WALLET'])
def test_owned_case_funding_and_reward_receipts_reject_changed_owner_amount_and_new_message_replay(case_game,mode):
    w=case_game
    first=open_case(w,mode=mode);deliver(w)
    with w['game'].transaction() as session:
        rows=[(r.id,r.destination,r.event_name,r.payload) for r in session.scalars(select(OutboxMessage).where(OutboxMessage.aggregate_id==UUID(first['openingId']),OutboxMessage.destination.in_(['wallet','inventory'])))]
    before_money,before_items=balances(w),items(w)
    for _,destination,event,payload in rows:
        participant=w['wallet'] if destination=='wallet' else w['inventory']
        original=participant.receive(uuid4(),event,1,payload)['receipt']
        assert original['openingId']==first['openingId']
        with pytest.raises((WalletError,InventoryError)) as error:
            participant.receive(uuid4(),event,1,{**payload,'accountId':str(uuid4())})
        assert error.value.status_code==409
        field='amount' if event==CASE_PAYMENT else 'quantity'
        with pytest.raises((WalletError,InventoryError)):
            participant.receive(uuid4(),event,1,{**payload,field:payload[field]+1})
    assert balances(w)==before_money and items(w)==before_items


def test_parallel_workers_finish_one_opening_without_duplicate_grants(case_game):
    w=case_game
    first=open_case(w)
    with ThreadPoolExecutor(max_workers=4) as pool:
        for _ in range(12):
            list(pool.map(lambda _:worker(w).once(),range(4)))
    assert get(w,first['openingId'])['status']=='COMPLETED'
    assert len(w['inventory'].get(w['account'])['Weapons'])==w['owned_before']+2


def test_http_open_poll_auth_ownership_and_server_owned_result(case_game):
    w=case_game;client=w['client'];headers={'Authorization':'Bearer test-token'}
    endpoint=f"/v1/cases/{w['case']}/open"
    assert client.post(endpoint,json={'requestKey':str(uuid4())}).status_code==401
    assert client.post(endpoint,headers=headers,json={'requestKey':str(uuid4()),'rarity':'RARE','price':0}).status_code==422
    response=client.post(endpoint,headers=headers,json={'requestKey':str(uuid4())})
    assert response.status_code==202
    identity=response.json()['openingId']
    assert client.get('/v1/case-openings/'+identity,headers=headers).status_code==200
    with pytest.raises(ApplicationError) as error:w['cases'].get(uuid4(),UUID(identity))
    assert error.value.status_code==404
    assert w['cases'].catalog()['cases'][0]['caseId']==str(w['case'])
    deliver(w)
    replay=client.post(endpoint,headers=headers,json={'requestKey':response.json()['requestKey']})
    assert replay.status_code==200 and replay.json()['replayed']


@pytest.mark.parametrize('mode,service',[('INVENTORY','inventory'),('WALLET','wallet')])
def test_participant_funding_commit_failure_rolls_back_debit_and_source_receipt(case_game,mode,service):
    w=case_game
    first=open_case(w,mode=mode)
    db=w[service+'_db']
    before_money,before_items=balances(w),items(w)
    with db.engine.begin() as conn:
        conn.execute(text('ALTER TABLE case_operation_receipts ADD CONSTRAINT injected_funding_receipt_failure CHECK (item_index <> -1)'))
    try:
        with pytest.raises(IntegrityError):worker(w).once()
        assert balances(w)==before_money and items(w)==before_items
        assert get(w,first['openingId'])['result'] is None
    finally:
        with db.engine.begin() as conn:conn.execute(text('ALTER TABLE case_operation_receipts DROP CONSTRAINT injected_funding_receipt_failure'))
    ready_again(w);deliver(w)
    assert get(w,first['openingId'])['status']=='COMPLETED'


@pytest.mark.parametrize('service,index',[('wallet',0),('inventory',2)])
def test_participant_reward_commit_failure_is_recoverable_without_duplicate_grants(case_game,service,index):
    w=case_game
    first=open_case(w)
    assert worker(w).once()
    db=w[service+'_db']
    with db.engine.begin() as conn:
        conn.execute(text(f'ALTER TABLE case_operation_receipts ADD CONSTRAINT injected_reward_receipt_failure CHECK (item_index <> {index})'))
    try:
        with pytest.raises(IntegrityError):deliver(w)
        pending=get(w,first['openingId'])
        assert pending['status']=='PENDING'
        original_result=pending['result']
    finally:
        with db.engine.begin() as conn:conn.execute(text('ALTER TABLE case_operation_receipts DROP CONSTRAINT injected_reward_receipt_failure'))
    ready_again(w);deliver(w)
    final=get(w,first['openingId'])
    assert final['status']=='COMPLETED' and final['result']==original_result
    assert balances(w)['GEMS']==w['money_before']['GEMS']+5
    assert len(w['inventory'].get(w['account'])['Weapons'])==w['owned_before']+2


def test_saved_private_seed_keeps_the_draw_stable_when_game_confirmation_rolls_back(case_game):
    from vatican_backend.domain.cases import seeded_randbelow
    w=case_game
    w['cases'].randbelow=None
    first=open_case(w)
    with w['game'].transaction() as session:
        row=session.get(CaseOpening,UUID(first['openingId']))
        seed=row.random_seed
        expected,_,_,roll,_=w['case_rules'].draw(0,lambda upper:seeded_randbelow(seed,upper))
    assert len(seed)==32 and 'randomSeed' not in first
    with w['game'].engine.begin() as conn:
        conn.execute(text("ALTER TABLE case_reward_grants ADD CONSTRAINT injected_seed_failure CHECK (quantity < 0)"))
    try:
        with pytest.raises(IntegrityError):worker(w).once()
    finally:
        with w['game'].engine.begin() as conn:conn.execute(text('ALTER TABLE case_reward_grants DROP CONSTRAINT injected_seed_failure'))
    ready_again(w);deliver(w)
    final=get(w,first['openingId'])
    assert final['result']['entryIndex']==expected.index and final['result']['roll']==roll


def test_stale_funding_lease_and_wrong_receipt_cannot_advance_an_opening(case_game):
    w=case_game
    first=open_case(w)
    with w['use_case'].uow() as work:message=work.messages.claim()
    receipt=CaseDelivery(w).deliver(message)
    ready_again(w)
    with w['use_case'].uow() as work:
        newer=work.messages.claim()
        assert newer['id']==message['id']
        assert not work.messages.confirm(message['id'],message['lease'],receipt)
    assert get(w,first['openingId'])['result'] is None
    with pytest.raises(ValueError):validate_case_receipt(CASE_CONSUME,message['payload'],{**receipt,'openingId':str(uuid4())})
    # Leave the current lease to expire; redelivery returns the saved debit receipt.
    ready_again(w);deliver(w)
    assert get(w,first['openingId'])['status']=='COMPLETED'


def test_persistent_local_settlement_failure_reaches_review_and_preserves_paid_case(case_game):
    w=case_game
    first=open_case(w)
    with w['game'].transaction() as session:
        session.get(OutboxMessage,UUID(first['paymentMessageId'])).attempts=19
    with w['game'].engine.begin() as conn:
        conn.execute(text("ALTER TABLE case_reward_grants ADD CONSTRAINT injected_persistent_failure CHECK (quantity < 0)"))
    try:
        with pytest.raises(IntegrityError):worker(w).once()
        assert items(w)['case:level-case']==0
        review=get(w,first['openingId'])
        assert review['status']=='REVIEW' and review['result'] is None
    finally:
        with w['game'].engine.begin() as conn:conn.execute(text('ALTER TABLE case_reward_grants DROP CONSTRAINT injected_persistent_failure'))
    assert w['client'].app.state.use_cases.rewards.requeue(UUID(first['paymentMessageId']))
    deliver(w)
    assert get(w,first['openingId'])['status']=='COMPLETED'
    assert items(w)['case:level-case']==0
