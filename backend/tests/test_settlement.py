from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from uuid import UUID, uuid4
import pytest
from sqlalchemy import select, func
from vatican_backend.bootstrap.settings import Settings, RewardPolicy, ServerSlot
from vatican_backend.bootstrap.weapon_catalog import LoadCatalog
from vatican_backend.application.commands import ServerRegistration, Assignment, RunResult
from vatican_backend.application.runs import RunUseCases
from vatican_backend.application.profiles import ProfileUseCases
from vatican_backend.application.weapons import WeaponDefinitionsUseCases
from vatican_backend.application.messaging import DeliverRewards
from vatican_backend.application.errors import ApplicationError
from vatican_backend.application.energy import ConfigureEnergy
from vatican_backend.domain.energy import EnergyRules
from vatican_backend.application.progression import ConfigureProgression
from vatican_backend.domain.progression import ProgressionRules, XPRule, LevelDefinition
from vatican_backend.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as GameUow
from vatican_backend.persistence.postgres.tables import OutboxMessage, Run
from vatican_rating.application.use_cases import RatingUseCases
from vatican_rating.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as RatingUow
from vatican_wallet.application.use_cases import WalletUseCases
from vatican_wallet.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as WalletUow
from vatican_inventory.application.use_cases import InventoryUseCases
from vatican_inventory.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as InventoryUow
from vatican_shop.application.use_cases import ShopUseCases, SettlePurchases
from vatican_shop.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as ShopUow


class Definitions:
    def get(self):
        return LoadCatalog()


@dataclass
class World:
    game_db: object
    wallet_db: object
    inventory_db: object
    shop_db: object
    runs: object
    wallet: object
    inventory: object
    account_id: UUID
    slot: object
    rating: object
    analytics: object


@pytest.fixture
def world(service_storage_factory,analytics_actor):
    game_db,url=service_storage_factory('game_backend')
    wallet_db,_=service_storage_factory('wallet')
    inventory_db,_=service_storage_factory('inventory')
    shop_db,_=service_storage_factory('shop')
    rating_db,_=service_storage_factory('rating')
    wallet=WalletUseCases(WalletUow(wallet_db))
    inventory=InventoryUseCases(InventoryUow(inventory_db),Definitions())
    account_id=uuid4()
    uow=GameUow(game_db)
    ProfileUseCases(uow).ensure(account_id)
    WeaponDefinitionsUseCases(uow,LoadCatalog()).register_catalog()
    slot=ServerSlot(uuid4(),'127.0.0.1',7979,'s'*32)
    ConfigureEnergy(uow).execute(EnergyRules('test-energy-v1',100,60000,1,5))
    ConfigureProgression(uow).execute(ProgressionRules('test-release',(XPRule('RUN_VICTORY','*',0,0),),(LevelDefinition(1,0),)))
    settings=Settings(url,{slot.server_id:slot},120,{'test-release':RewardPolicy(25)},energy_ruleset_id='test-energy-v1')
    class InventoryGateway:
        def snapshot(self, account):
            assert game_db.engine.pool.checkedout()==0
            return inventory.get(account,selected_only=True)
    runs=RunUseCases(uow,settings,InventoryGateway())
    return World(game_db,wallet_db,inventory_db,shop_db,runs,wallet,inventory,account_id,slot,RatingUseCases(RatingUow(rating_db)),analytics_actor)


def complete(world,outcome='victory'):
    w=world
    w.runs.register_server(w.slot.server_id,w.slot.secret,ServerRegistration('test-release',3,'udp-loopback'))
    pending=w.runs.assign(w.account_id,respond_async=True)
    assignment=w.runs.server_assignment(w.slot.server_id,w.slot.secret)
    w.runs.acknowledge(w.slot.server_id,w.slot.secret,Assignment(UUID(pending['runId']),assignment['generation']))
    result=RunResult(UUID(pending['runId']),assignment['generation'],w.account_id,'test-release',outcome,1)
    reply=w.runs.apply_result(w.slot.server_id,w.slot.secret,result)
    return result,reply


class Delivery:
    def __init__(self,world,lose_ack=False):
        self.world,self.lose_ack=world,lose_ack
    def deliver(self,message):
        w=self.world
        assert w.game_db.engine.pool.checkedout()==0
        participant={'wallet':w.wallet,'inventory':w.inventory,'rating':w.rating,'analytics':w.analytics}[message['destination']]
        reply=participant.receive(message['id'],message['event_name'],message['schema_version'],message['payload'])
        if self.lose_ack:
            raise ApplicationError(503,'Acknowledgement lost')
        return reply.get('receipt')


def drain(world,delivery=None):
    worker=DeliverRewards(GameUow(world.game_db),delivery or Delivery(world))
    while worker.once():
        pass


def test_results_and_reward_delivery_are_idempotent_under_concurrent_repeats(world):
    result,reply=complete(world)
    assert reply['rewardStatus']=='PENDING'
    with ThreadPoolExecutor(max_workers=4) as pool:
        replies=list(pool.map(lambda _:world.runs.apply_result(world.slot.server_id,world.slot.secret,result),range(4)))
    assert all(r['replayed'] for r in replies)
    with world.game_db.transaction() as session:
        assert session.scalar(select(func.count()).select_from(OutboxMessage))==4
    drain(world)
    drain(world)
    assert world.wallet.balance(world.account_id)['balance']==525
    weapons=world.inventory.get(world.account_id)['Weapons']
    assert len(weapons)==2
    starter=next(w for w in weapons if w['InstanceId']==str(__import__('uuid').uuid5(world.account_id,'weapon.starter.sword')))
    assert starter['Level']==2
    assert world.runs.assignment_status(result.run_id,world.account_id)['rewardStatus']=='COMPLETED'


def test_lost_ack_retries_without_second_credit_weapon_or_xp(world):
    result,_=complete(world)
    drain(world,Delivery(world,lose_ack=True))
    assert world.wallet.balance(world.account_id)['balance']==525
    before=world.inventory.get(world.account_id)
    with world.game_db.transaction() as session:
        for row in session.scalars(select(OutboxMessage)):
            assert row.published_at is None
            row.available_at=datetime.now(timezone.utc)-timedelta(seconds=1)
    drain(world)
    assert world.wallet.balance(world.account_id)['balance']==525
    assert world.inventory.get(world.account_id)==before
    assert world.runs.assignment_status(result.run_id,world.account_id)['rewardStatus']=='COMPLETED'


def test_defeat_does_not_credit_prestige(world):
    _,reply=complete(world,outcome='defeat')
    assert reply['permanentReward']==0
    drain(world)
    assert world.wallet.balance(world.account_id)['balance']==500


def test_failed_delivery_is_retained_and_visible(world):
    result,_=complete(world)
    class Reject:
        def deliver(self,message):
            raise ApplicationError(422,'Unsupported schema')
    drain(world,Reject())
    with world.game_db.transaction() as session:
        assert all(row.failed_at and not row.published_at for row in session.scalars(select(OutboxMessage)))
    assert world.runs.assignment_status(result.run_id,world.account_id)['rewardStatus']=='FAILED'


def test_old_worker_cannot_confirm_a_renewed_lease(world):
    complete(world)
    uow=GameUow(world.game_db)
    with uow() as work:
        first=work.messages.claim()
    with world.game_db.transaction() as session:
        session.get(OutboxMessage,first['id']).available_at=datetime.now(timezone.utc)-timedelta(seconds=1)
    with uow() as work:
        second=work.messages.claim()
        assert second['id']==first['id']
        work.messages.confirm(first['id'],first['lease'])
    with world.game_db.transaction() as session:
        assert session.get(OutboxMessage,first['id']).published_at is None
    with uow() as work:
        work.messages.confirm(second['id'],second['lease'])
    with world.game_db.transaction() as session:
        assert session.get(OutboxMessage,first['id']).published_at is not None


def test_same_message_id_with_changed_payload_is_rejected(world):
    message_id,run_id=uuid4(),uuid4()
    payload={'accountId':str(world.account_id),'runId':str(run_id),'amount':25}
    world.wallet.receive(message_id,'run.currency_reward',1,payload)
    from vatican_wallet.application.errors import ApplicationError as WalletError
    with pytest.raises(WalletError):
        world.wallet.receive(message_id,'run.currency_reward',1,{**payload,'amount':100})
    assert world.wallet.balance(world.account_id)['balance']==525


def test_concurrent_first_wallet_access_and_repeated_charges_do_not_duplicate_initial_balance(world):
    operation=uuid4()
    with ThreadPoolExecutor(max_workers=4) as pool:
        results=list(pool.map(lambda _:world.wallet.change(world.account_id,operation,25),range(4)))
    assert len({r['balance'] for r in results})==1
    assert world.wallet.balance(world.account_id)['balance']==475
    other=uuid4()
    assert world.wallet.change(other,operation,25)['balance']==475


def test_rejected_charge_is_stable_after_later_funding_and_refund_requires_charge(world):
    operation=uuid4()
    first=world.wallet.change(world.account_id,operation,600)
    assert first['status']=='REJECTED'
    world.wallet.receive(uuid4(),'run.currency_reward',1,{'accountId':str(world.account_id),'runId':str(uuid4()),'amount':500})
    assert world.wallet.change(world.account_id,operation,600)==first
    assert world.wallet.change(world.account_id,uuid4(),10,refund=True)['status']=='REJECTED'
    assert world.wallet.balance(world.account_id)['balance']==1000


class Catalog:
    def __init__(self,price=25,limit=1):
        self.price,self.limit=price,limit
    def get(self):
        return {'version':'test-v1','offers':[{'id':'sword','weaponConfigurationId':'sword.common','price':self.price,
                'purchaseLimit':self.limit,'available':True,'unavailableReason':''}]}


class PurchaseGateway:
    def __init__(self,world,fail_grant=False,lose_ack=False):
        self.world,self.fail_grant,self.lose_ack=world,fail_grant,lose_ack
    def charge(self,job):
        assert self.world.shop_db.engine.pool.checkedout()==0
        result=self.world.wallet.change(job['accountId'],job['operationId'],job['plan']['price'])
        if self.lose_ack:
            from vatican_shop.application.errors import ApplicationError as ShopError
            self.lose_ack=False
            raise ShopError(503,'Response lost')
        return result
    def grant(self,job):
        assert self.world.shop_db.engine.pool.checkedout()==0
        if self.fail_grant:
            return {'status':'REJECTED','code':'UnknownWeaponConfiguration'}
        return self.world.inventory.grant(job['accountId'],job['operationId'],job['plan']['configurationId'])
    def refund(self,job):
        return self.world.wallet.change(job['accountId'],job['operationId'],job['plan']['price'],refund=True)


def test_shop_recovers_lost_charge_ack_and_grants_once(world):
    uow=ShopUow(world.shop_db)
    shop=ShopUseCases(uow,Catalog())
    operation=uuid4()
    assert shop.purchase(world.account_id,operation,'sword','test-v1')['status']=='Pending'
    worker=SettlePurchases(uow,PurchaseGateway(world,lose_ack=True))
    worker.once()
    assert world.wallet.balance(world.account_id)['balance']==475
    from vatican_shop.persistence.postgres.tables import PurchaseJob
    with world.shop_db.transaction() as session:
        session.get(PurchaseJob,(world.account_id,operation)).available_at=datetime.now(timezone.utc)-timedelta(seconds=1)
    while worker.once():
        pass
    assert shop.receipt(world.account_id,operation)['status']=='Completed'
    assert world.wallet.balance(world.account_id)['balance']==475
    assert len(world.inventory.get(world.account_id)['Weapons'])==2
    assert shop.purchase(world.account_id,operation,'sword','test-v1')['replayed'] is True


def test_shop_refunds_when_inventory_permanently_rejects(world):
    uow=ShopUow(world.shop_db)
    shop=ShopUseCases(uow,Catalog())
    operation=uuid4()
    shop.purchase(world.account_id,operation,'sword','test-v1')
    worker=SettlePurchases(uow,PurchaseGateway(world,fail_grant=True))
    while worker.once():
        pass
    assert shop.receipt(world.account_id,operation)['status']=='Rejected'
    assert world.wallet.balance(world.account_id)['balance']==500
    assert len(world.inventory.get(world.account_id)['Weapons'])==1


def test_shop_concurrent_operations_reserve_purchase_limit(world):
    shop=ShopUseCases(ShopUow(world.shop_db),Catalog(limit=1))
    with ThreadPoolExecutor(max_workers=2) as pool:
        result=list(pool.map(lambda _:shop.purchase(world.account_id,uuid4(),'sword','test-v1'),range(2)))
    assert sorted(r['status'] for r in result)==['Pending','Rejected']


def test_failed_rewards_can_be_requeued_without_duplicate_grants(world):
    result,_=complete(world)
    class Reject:
        def deliver(self,message):
            raise ApplicationError(422,'Unsupported schema')
    drain(world,Reject())
    worker=DeliverRewards(GameUow(world.game_db),Delivery(world))
    with world.game_db.transaction() as session:
        ids=list(session.scalars(select(OutboxMessage.id)))
    assert all(worker.requeue(message_id) for message_id in ids)
    drain(world)
    assert not worker.requeue(ids[0])
    assert world.wallet.balance(world.account_id)['balance']==525
    assert world.runs.assignment_status(result.run_id,world.account_id)['rewardStatus']=='COMPLETED'


def test_review_purchase_preserves_its_step_for_manual_reconciliation(world):
    from vatican_shop.persistence.postgres.tables import PurchaseJob
    from vatican_shop.application.errors import ApplicationError as ShopError
    uow=ShopUow(world.shop_db)
    shop=ShopUseCases(uow,Catalog())
    operation=uuid4()
    shop.purchase(world.account_id,operation,'sword','test-v1')
    class Unavailable(PurchaseGateway):
        def grant(self,job):
            raise ShopError(503,'Unavailable')
    worker=SettlePurchases(uow,Unavailable(world))
    worker.once()  # The charge commits; grants can still fail later.
    with world.shop_db.transaction() as session:
        session.get(PurchaseJob,(world.account_id,operation)).attempts=19
    worker.once()
    receipt=shop.receipt(world.account_id,operation)
    assert receipt['status']=='Review'
    assert '_resumeState' not in receipt
    assert world.wallet.balance(world.account_id)['balance']==475
    worker=SettlePurchases(uow,PurchaseGateway(world))
    assert worker.requeue(world.account_id,operation)
    while worker.once():
        pass
    assert shop.receipt(world.account_id,operation)['status']=='Completed'
    assert world.wallet.balance(world.account_id)['balance']==475
