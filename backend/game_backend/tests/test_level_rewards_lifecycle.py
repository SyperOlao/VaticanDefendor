from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager
from threading import local
from datetime import datetime, timedelta, timezone
from uuid import UUID,uuid4

import pytest
from sqlalchemy import func,select,text
from sqlalchemy.exc import IntegrityError
from vatican_backend.application.errors import ApplicationError
from vatican_backend.application.messaging import DeliverRewards
from vatican_backend.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as GameUow
from vatican_backend.persistence.postgres.tables import (CaseType,LevelRewardGrant,OutboxMessage,PlayerLevelRewardClaim,
    RewardBundleItem,PlayerEnergy)
from vatican_backend.domain.level_rewards import LEVEL_REWARD_EVENT
from vatican_analytics.application.errors import ApplicationError as AnalyticsError
from vatican_rating.application.use_cases import RatingUseCases
from vatican_rating.application.errors import ApplicationError as RatingError
from vatican_rating.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as RatingUow
from vatican_wallet.application.use_cases import WalletUseCases
from vatican_wallet.application.errors import ApplicationError as WalletError
from vatican_wallet.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as WalletUow
from vatican_wallet.persistence.postgres.tables import EconomyTransaction,CurrencyBalance,LevelRewardReceipt as WalletReceipt
from vatican_inventory.application.use_cases import InventoryUseCases
from vatican_inventory.application.errors import ApplicationError as InventoryError
from vatican_inventory.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as InventoryUow
from vatican_inventory.persistence.postgres.tables import OwnedWeapon,LevelRewardReceipt as InventoryReceipt
from .test_progression_lifecycle import xp_game,start,apply


class Delivery:
    def __init__(self, world, down=None, lost=None, reject=None):
        self.world,self.down,self.lost,self.reject=world,down,lost,reject
    def deliver(self,message):
        w=self.world
        assert getattr(w['active'],'game',0)==0
        participant={'wallet':w['wallet'],'inventory':w['inventory'],'rating':w['rating'],'analytics':w['analytics']}[message['destination']]
        is_level=message['event_name']==LEVEL_REWARD_EVENT
        if is_level and message['destination']==self.down:
            raise ApplicationError(503,'Participant unavailable')
        if is_level and message['destination']==self.reject:
            raise ApplicationError(422,'Participant rejected grant')
        try:
            ack=participant.receive(message['id'],message['event_name'],message['schema_version'],message['payload'])
        except (WalletError,InventoryError,RatingError,AnalyticsError) as error:
            raise ApplicationError(error.status_code,'Participant rejected grant') from None
        if is_level and message['destination']==self.lost:
            raise ApplicationError(503,'Acknowledgement lost after participant commit')
        return ack.get('receipt')


def drain(world,delivery=None):
    worker=DeliverRewards(GameUow(world['game']),delivery or Delivery(world))
    for _ in range(30):
        if not worker.once():
            return
    raise AssertionError('Delivery did not quiesce')


def ready_again(world):
    with world['game'].transaction() as session:
        for row in session.scalars(select(OutboxMessage).where(OutboxMessage.published_at.is_(None))):
            row.available_at=datetime.now(timezone.utc)-timedelta(seconds=1)


@pytest.fixture
def gift_game(xp_game,wallet_storage,inventory_storage,rating_storage,analytics_actor):
    client,slot,account,rules=xp_game
    bundle=rules.levels[1].reward_bundle_id
    with client.app.state.database.transaction() as session:
        session.add(CaseType(code='level-case'))
        session.add_all([
            RewardBundleItem(bundle_id=bundle,item_index=1,reward_type='CURRENCY',reward_ref='GEMS',quantity=7),
            RewardBundleItem(bundle_id=bundle,item_index=2,reward_type='WEAPON',reward_ref='sword.common',quantity=2),
            RewardBundleItem(bundle_id=bundle,item_index=3,reward_type='ITEM',reward_ref='repair-kit',quantity=3),
            RewardBundleItem(bundle_id=bundle,item_index=4,reward_type='CASE',reward_ref='level-case',quantity=1),
            RewardBundleItem(bundle_id=bundle,item_index=5,reward_type='ENERGY',reward_ref='ENERGY',quantity=20)])
    game=client.app.state.database
    active=local()
    def track(database, name):
        original=database.transaction
        @contextmanager
        def transaction():
            setattr(active,name,getattr(active,name,0)+1)
            try:
                with original() as session:
                    yield session
            finally:
                setattr(active,name,getattr(active,name)-1)
        database.transaction=transaction
    track(game,'game')
    track(inventory_storage[0],'inventory')
    class Definitions:
        def get(self):
            assert getattr(active,'game',0)==0
            assert getattr(active,'inventory',0)==0
            return client.app.state.use_cases.weapons.get()
    wallet=WalletUseCases(WalletUow(wallet_storage[0]))
    inventory=InventoryUseCases(InventoryUow(inventory_storage[0]),Definitions())
    reply=apply(xp_game,start(xp_game))
    claim_id=UUID(reply['progression']['unlockedRewards'][0]['claimId'])
    world={'client':client,'game':game,'active':active,'wallet_db':wallet_storage[0],'inventory_db':inventory_storage[0],'rating_db':rating_storage[0],
           'wallet':wallet,'inventory':inventory,'rating':RatingUseCases(RatingUow(rating_storage[0])),'analytics':analytics_actor,'account':account,'claim':claim_id,'bundle':bundle,
           'use_case':client.app.state.use_cases.level_rewards}
    drain(world)  # Finish the ordinary run rewards before testing the separate level gift.
    world['balance_before']=wallet.balance(account)['balance']
    world['weapons_before']=len(inventory.get(account)['Weapons'])
    return world


def get(w):
    return w['use_case'].get(w['account'],w['claim'])


def claim(w):
    return w['use_case'].claim(w['account'],w['claim'])


def balances(w):
    return {r['currencyId']:r['balance'] for r in w['wallet'].balance(w['account'])['balances']}


def test_claim_freezes_plan_and_completes_only_after_every_remote_line_is_acknowledged(gift_game):
    w=gift_game
    assert get(w)['status']=='AVAILABLE'
    result=claim(w)
    assert result['status']=='PENDING' and result['deliveryStatus']=='PARTIAL'
    assert w['client'].app.state.use_cases.energy.get(w['account'])['currentEnergy']==115
    assert len(result['lines'])==6 and sum(r['status']=='COMPLETED' for r in result['lines'])==1
    drain(w)
    complete=get(w)
    assert complete['status']=='CLAIMED' and complete['deliveryStatus']=='COMPLETED'
    assert complete['claimedAt'] is not None and all(r['status']=='COMPLETED' for r in complete['lines'])
    assert balances(w)=={'GEMS':7,'PRESTIGE':w['balance_before']+10}
    assert len(w['inventory'].get(w['account'])['Weapons'])==w['weapons_before']+2
    assert {i['itemId']:i['quantity'] for i in w['inventory'].items(w['account'])['items']}=={'case:level-case':1,'item:repair-kit':3}
    repeated=claim(w)
    assert repeated['replayed'] and repeated['lines']==complete['lines']
    assert balances(w)['PRESTIGE']==w['balance_before']+10


def test_concurrent_claim_requests_create_one_line_plan_and_do_not_repeat_local_energy(gift_game):
    w=gift_game
    with ThreadPoolExecutor(max_workers=5) as workers:
        replies=list(workers.map(lambda _:claim(w),range(5)))
    assert sum(not r['replayed'] for r in replies)==1
    with w['game'].transaction() as session:
        assert session.scalar(select(func.count()).select_from(LevelRewardGrant).where(LevelRewardGrant.claim_id==w['claim']))==6
        assert session.scalar(select(func.count()).select_from(OutboxMessage).where(OutboxMessage.aggregate_id==w['claim']))==5
    assert w['client'].app.state.use_cases.energy.get(w['account'])['currentEnergy']==115
    drain(w)
    assert get(w)['status']=='CLAIMED'


@pytest.mark.parametrize('down',['wallet','inventory'])
def test_partial_participant_outage_preserves_completed_lines_and_resumes_after_recovery(gift_game,down):
    w=gift_game
    claim(w)
    drain(w,Delivery(w,down=down))
    partial=get(w)
    assert partial['status']=='PENDING' and partial['deliveryStatus']=='PARTIAL'
    completed_ids={r['itemIndex']:r['receipt'] for r in partial['lines'] if r['status']=='COMPLETED'}
    if down=='inventory':
        assert balances(w)['PRESTIGE']==w['balance_before']+10 and balances(w)['GEMS']==7
        assert len(w['inventory'].get(w['account'])['Weapons'])==w['weapons_before']
    else:
        assert balances(w)['PRESTIGE']==w['balance_before']
        assert len(w['inventory'].get(w['account'])['Weapons'])==w['weapons_before']+2
    assert claim(w)['replayed']
    ready_again(w)
    drain(w)  # New worker instance, same stored source lines.
    final=get(w)
    assert final['status']=='CLAIMED'
    for line in final['lines']:
        if line['itemIndex'] in completed_ids:
            assert line['receipt']==completed_ids[line['itemIndex']]
    assert balances(w)=={'GEMS':7,'PRESTIGE':w['balance_before']+10}
    assert len(w['inventory'].get(w['account'])['Weapons'])==w['weapons_before']+2


@pytest.mark.parametrize('lost',['wallet','inventory'])
def test_lost_ack_after_participant_commit_does_not_duplicate_money_or_weapon_instances(gift_game,lost):
    w=gift_game
    claim(w)
    drain(w,Delivery(w,lost=lost))
    before_money=balances(w)
    before_items=w['inventory'].get(w['account'])
    before_stacks=w['inventory'].items(w['account'])
    assert get(w)['status']=='PENDING'
    ready_again(w)
    drain(w)
    assert get(w)['status']=='CLAIMED'
    assert balances(w)==before_money
    assert w['inventory'].get(w['account'])==before_items
    assert w['inventory'].items(w['account'])==before_stacks


def test_permanent_error_requires_review_and_redrive_never_repeats_completed_wallet_lines(gift_game):
    w=gift_game
    claim(w)
    drain(w,Delivery(w,reject='inventory'))
    review=get(w)
    assert review['status']=='REVIEW' and review['requiresReview'] and review['claimedAt'] is None
    assert balances(w)['PRESTIGE']==w['balance_before']+10
    worker=DeliverRewards(GameUow(w['game']),Delivery(w))
    failed=[UUID(r['messageId']) for r in review['lines'] if r['status']=='FAILED']
    assert len(failed)==3
    assert all(worker.requeue(identity) for identity in failed)
    assert get(w)['status']=='PENDING'
    drain(w)
    assert get(w)['status']=='CLAIMED'
    assert balances(w)['PRESTIGE']==w['balance_before']+10
    assert not worker.requeue(failed[0])


def test_maximum_retries_keeps_receipts_for_manual_reconciliation(gift_game):
    w=gift_game
    claim(w)
    with w['game'].transaction() as session:
        for row in session.scalars(select(OutboxMessage).where(OutboxMessage.aggregate_id==w['claim'],OutboxMessage.destination=='inventory')):
            row.attempts=19
    drain(w,Delivery(w,down='inventory'))
    assert get(w)['status']=='REVIEW'
    assert balances(w)['PRESTIGE']==w['balance_before']+10


def test_bundle_changes_after_unlock_cannot_change_the_promised_gift(gift_game):
    w=gift_game
    with w['game'].transaction() as session:
        session.get(RewardBundleItem,(w['bundle'],0)).quantity=999
        session.get(RewardBundleItem,(w['bundle'],2)).reward_ref='axe.common'
    assert get(w)['lines'][0]['quantity']==10
    claim(w)
    drain(w)
    assert balances(w)['PRESTIGE']==w['balance_before']+10
    assert get(w)['lines'][2]['rewardRef']=='sword.common'


def test_another_player_cannot_claim_or_read_the_reward(gift_game):
    w=gift_game
    other=uuid4()
    w['client'].app.state.use_cases.profiles.ensure(other)
    with pytest.raises(ApplicationError) as error:
        w['use_case'].claim(other,w['claim'])
    assert error.value.status_code==404
    with pytest.raises(ApplicationError):
        w['use_case'].get(other,w['claim'])
    assert get(w)['status']=='AVAILABLE'


def test_http_claim_and_poll_are_idempotent_and_client_cannot_override_the_bundle(gift_game):
    w=gift_game
    client=w['client']
    prefix='/v1/level-rewards/'+str(w['claim'])
    assert client.post(prefix+'/claim').status_code==401
    headers={'Authorization':'Bearer test-token'}
    assert client.post(prefix+'/claim',headers=headers,json={'quantity':999}).status_code==422
    response=client.post(prefix+'/claim',headers=headers)
    assert response.status_code==202 and response.json()['status']=='PENDING'
    assert client.post(prefix+'/claim',headers=headers).json()['replayed']
    assert client.get('/v1/level-rewards',headers=headers).json()['claims'][0]['claimId']==str(w['claim'])
    drain(w)
    assert client.get(prefix,headers=headers).json()['status']=='CLAIMED'
    assert client.post(prefix+'/claim',headers=headers).status_code==200


def test_failed_preparation_rolls_back_local_credit_and_all_jobs(gift_game,monkeypatch):
    from vatican_backend.persistence.postgres.repositories.level_rewards import LevelRewardsRepository
    w=gift_game
    original=LevelRewardsRepository.finalize
    def fail(*args,**kwargs):
        raise RuntimeError('Injected commit failure')
    monkeypatch.setattr(LevelRewardsRepository,'finalize',fail)
    with pytest.raises(RuntimeError):
        claim(w)
    assert get(w)['status']=='AVAILABLE'
    assert w['client'].app.state.use_cases.energy.get(w['account'])['currentEnergy']==95
    with w['game'].transaction() as session:
        assert session.scalar(select(func.count()).select_from(LevelRewardGrant).where(LevelRewardGrant.claim_id==w['claim']))==0
        assert session.scalar(select(func.count()).select_from(OutboxMessage).where(OutboxMessage.aggregate_id==w['claim']))==0
    monkeypatch.setattr(LevelRewardsRepository,'finalize',original)
    assert claim(w)['status']=='PENDING'


def test_source_receipts_protect_against_changed_message_ids_payloads_and_owner(gift_game):
    w=gift_game
    claim(w)
    drain(w)
    with w['game'].transaction() as session:
        messages=[{'id':r.id,'destination':r.destination,'event_name':r.event_name,'schema_version':r.schema_version,'payload':r.payload}
                  for r in session.scalars(select(OutboxMessage).where(OutboxMessage.aggregate_id==w['claim'],OutboxMessage.event_name==LEVEL_REWARD_EVENT))]
    for message in messages:
        participant={'wallet':w['wallet'],'inventory':w['inventory'],'rating':w['rating'],'analytics':w['analytics']}[message['destination']]
        first=participant.receive(uuid4(),LEVEL_REWARD_EVENT,1,message['payload'])
        assert first['status']=='APPLIED'
        for patch in ({'quantity':message['payload']['quantity']+1},{'accountId':str(uuid4())}):
            with pytest.raises((WalletError,InventoryError)):
                participant.receive(uuid4(),LEVEL_REWARD_EVENT,1,{**message['payload'],**patch})
    assert balances(w)=={'GEMS':7,'PRESTIGE':w['balance_before']+10}
    assert len(w['inventory'].get(w['account'])['Weapons'])==w['weapons_before']+2


def test_parallel_workers_complete_one_claim_without_double_grants(gift_game):
    w=gift_game
    claim(w)
    with ThreadPoolExecutor(max_workers=5) as workers:
        list(workers.map(lambda _:drain(w),range(5)))
    assert get(w)['status']=='CLAIMED'
    assert balances(w)=={'GEMS':7,'PRESTIGE':w['balance_before']+10}
    assert len(w['inventory'].get(w['account'])['Weapons'])==w['weapons_before']+2


def test_wallet_commit_failure_rolls_back_credit_and_source_receipt(gift_game):
    w=gift_game
    claim(w)
    with w['game'].transaction() as session:
        row=session.scalar(select(OutboxMessage).where(OutboxMessage.aggregate_id==w['claim'],OutboxMessage.destination=='wallet').order_by(OutboxMessage.payload['itemIndex'].as_integer()))
        message_id,payload=row.id,row.payload
    with w['wallet_db'].engine.begin() as connection:
        connection.execute(text('ALTER TABLE level_reward_receipts ADD CONSTRAINT injected_receipt_failure CHECK (item_index <> 0)'))
    try:
        with pytest.raises(IntegrityError):
            w['wallet'].receive(message_id,LEVEL_REWARD_EVENT,1,payload)
        assert balances(w)['PRESTIGE']==w['balance_before']
        with w['wallet_db'].transaction() as session:
            assert session.get(WalletReceipt,(w['claim'],0)) is None
    finally:
        with w['wallet_db'].engine.begin() as connection:
            connection.execute(text('ALTER TABLE level_reward_receipts DROP CONSTRAINT injected_receipt_failure'))
    drain(w)
    assert balances(w)['PRESTIGE']==w['balance_before']+10
    assert get(w)['status']=='CLAIMED'


def test_inventory_commit_failure_rolls_back_instances_and_source_receipt(gift_game):
    w=gift_game
    claim(w)
    with w['game'].transaction() as session:
        row=session.scalar(select(OutboxMessage).where(OutboxMessage.aggregate_id==w['claim'],OutboxMessage.payload['itemIndex'].as_integer()==2))
        message_id,payload=row.id,row.payload
    with w['inventory_db'].engine.begin() as connection:
        connection.execute(text('ALTER TABLE level_reward_receipts ADD CONSTRAINT injected_receipt_failure CHECK (item_index <> 2)'))
    try:
        with pytest.raises(IntegrityError):
            w['inventory'].receive(message_id,LEVEL_REWARD_EVENT,1,payload)
        assert len(w['inventory'].get(w['account'])['Weapons'])==w['weapons_before']
        with w['inventory_db'].transaction() as session:
            assert session.get(InventoryReceipt,(w['claim'],2)) is None
    finally:
        with w['inventory_db'].engine.begin() as connection:
            connection.execute(text('ALTER TABLE level_reward_receipts DROP CONSTRAINT injected_receipt_failure'))
    drain(w)
    assert get(w)['status']=='CLAIMED'
    assert len(w['inventory'].get(w['account'])['Weapons'])==w['weapons_before']+2


def test_game_confirmation_failure_can_be_retried_after_remote_grant_commit(gift_game):
    w=gift_game
    claim(w)
    with w['game'].engine.begin() as connection:
        connection.execute(text("ALTER TABLE player_level_reward_claims ADD CONSTRAINT injected_completion_failure CHECK (status <> 'CLAIMED')"))
    try:
        with pytest.raises(IntegrityError):
            drain(w)
        before_money=balances(w)
        before_weapons=w['inventory'].get(w['account'])
        assert get(w)['status']=='PENDING'
    finally:
        with w['game'].engine.begin() as connection:
            connection.execute(text('ALTER TABLE player_level_reward_claims DROP CONSTRAINT injected_completion_failure'))
    ready_again(w)
    drain(w)
    assert get(w)['status']=='CLAIMED'
    assert balances(w)==before_money
    assert w['inventory'].get(w['account'])==before_weapons


def test_stale_delivery_lease_cannot_complete_a_line_or_claim(gift_game):
    w=gift_game
    claim(w)
    uow=GameUow(w['game'])
    with uow() as work:
        first=work.messages.claim()
    receipt=Delivery(w).deliver(first)
    ready_again(w)
    with uow() as work:
        second=work.messages.claim()
        assert second['id']==first['id']
        work.messages.confirm(first['id'],first['lease'],receipt)
    assert get(w)['status']=='PENDING'
    assert next(r for r in get(w)['lines'] if r['messageId']==str(first['id']))['status']=='PENDING'
    with uow() as work:
        work.messages.confirm(second['id'],second['lease'],receipt)
    drain(w)
    assert get(w)['status']=='CLAIMED'
    assert balances(w)['PRESTIGE']==w['balance_before']+10


def test_wrong_acknowledgement_cannot_mark_a_grant_complete(gift_game):
    w=gift_game
    claim(w)
    class WrongReceipt(Delivery):
        def deliver(self,message):
            receipt=super().deliver(message)
            if message['event_name']==LEVEL_REWARD_EVENT:
                receipt={**receipt,'claimId':str(uuid4())}
            return receipt
    drain(w,WrongReceipt(w))
    assert get(w)['status']=='PENDING'
    assert all(r['status']=='PENDING' for r in get(w)['lines'] if r['messageId'])
    before=balances(w)
    ready_again(w)
    drain(w)
    assert get(w)['status']=='CLAIMED' and balances(w)==before


def test_completed_inventory_receipt_can_be_replayed_when_content_service_is_down(gift_game):
    w=gift_game
    claim(w)
    drain(w)
    with w['game'].transaction() as session:
        row=session.scalar(select(OutboxMessage).where(OutboxMessage.aggregate_id==w['claim'],OutboxMessage.payload['itemIndex'].as_integer()==2))
        identity,payload=row.id,row.payload
    class Down:
        def get(self):
            raise InventoryError(503,'Content unavailable')
    w['inventory'].level_rewards.definitions=Down()
    assert w['inventory'].receive(identity,LEVEL_REWARD_EVENT,1,payload)['status']=='APPLIED'


def test_unsupported_reward_rejects_the_whole_plan_before_any_side_effect(gift_game):
    w=gift_game
    with w['game'].transaction() as session:
        row=session.get(PlayerLevelRewardClaim,w['claim'])
        row.reward_snapshot={**row.reward_snapshot,'items':[{**line,'rewardType':'EFFECT'} if line['itemIndex']==5 else line for line in row.reward_snapshot['items']]}
    with pytest.raises(ApplicationError) as error:
        claim(w)
    assert error.value.code=='InvalidRewardBundle'
    assert get(w)['status']=='AVAILABLE'
    assert w['client'].app.state.use_cases.energy.get(w['account'])['currentEnergy']==95
    assert balances(w)['PRESTIGE']==w['balance_before']
    with w['game'].transaction() as session:
        assert session.scalar(select(func.count()).select_from(OutboxMessage).where(OutboxMessage.aggregate_id==w['claim']))==0


def test_bundle_publication_is_versioned_and_immutable(gift_game):
    from vatican_backend.application.level_rewards import PublishRewardBundle
    from vatican_backend.domain.level_rewards import RewardPlan,RewardLine
    w=gift_game
    publish=PublishRewardBundle(GameUow(w['game']))
    bundle=uuid4()
    plan=RewardPlan(bundle,(RewardLine(0,'CURRENCY','GEMS',2),))
    publish.execute('test-publication',1,plan)
    publish.execute('test-publication',1,plan)
    with pytest.raises(ApplicationError,match='immutable'):
        publish.execute('test-publication',1,RewardPlan(bundle,(RewardLine(0,'CURRENCY','GEMS',200),)))
    with w['game'].transaction() as session:
        assert session.get(RewardBundleItem,(bundle,0)).quantity==2
