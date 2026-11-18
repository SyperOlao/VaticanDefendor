from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass, replace
from datetime import datetime, timedelta, timezone
from decimal import Decimal
from threading import Barrier
from uuid import UUID, uuid4

import pytest
from sqlalchemy import func, select, text
from sqlalchemy.exc import IntegrityError
from vatican_backend.application.commands import Assignment, Checkpoint, ServerRegistration
from vatican_backend.application.energy import ConfigureEnergy, EnergyService, EnergyUseCases
from vatican_backend.application.errors import ApplicationError
from vatican_backend.application.runs import RunUseCases
from vatican_backend.domain.energy import EnergyRules
from vatican_backend.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory
from vatican_backend.persistence.postgres.tables import (EnergyConfig, GameServer, IdempotencyRecord,
    PlayerEffect, PlayerEnergy, Ruleset, Run, RunEnergyCharge)
from .support import player_for, server_header


@dataclass
class FakeClock:
    at: datetime
    def now(self):
        return self.at
    def advance(self, seconds):
        self.at += timedelta(seconds=seconds)


@pytest.fixture
def energy_game(backend):
    client,settings,slot=backend
    account=UUID(player_for(client)['accountId'])
    clock=FakeClock(datetime.now(timezone.utc))
    client.app.state.use_cases.runs.clock=clock
    client.app.state.use_cases.energy.clock=clock
    client.app.state.use_cases.runs.register_server(slot.server_id,slot.secret,
        ServerRegistration('sha256-test-release',3,'udp-loopback'))
    return client,settings,slot,account,clock


def services(world):
    return world[0].app.state.use_cases


def balance(world):
    return services(world).energy.get(world[3])['currentEnergy']


def reserve(world,key=None):
    return services(world).runs.assign(world[3],respond_async=True,request_key=key or uuid4())


def ack(world,pending):
    client,_,slot,_,_=world
    assignment=services(world).runs.server_assignment(slot.server_id,slot.secret)
    return services(world).runs.acknowledge(slot.server_id,slot.secret,
        Assignment(UUID(pending['runId']),assignment['generation']))


def test_energy_api_authenticates_and_returns_server_snapshot(energy_game):
    client,_,_,account,_=energy_game
    assert client.get('/v1/energy').status_code==401
    response=client.get('/v1/energy',headers={'Authorization':'Bearer test-token'})
    assert response.status_code==200
    body=response.json()
    assert body['accountId']==str(account)
    assert body['currentEnergy']==body['maxEnergy']==100
    assert body['runCost']==5
    assert datetime.fromisoformat(body['calculatedAt']).utcoffset()==timedelta(0)
    assert client.get('/v1/energy?accountId='+str(uuid4()),headers={'Authorization':'Bearer test-token'}).json()['accountId']==str(account)


def test_concurrent_first_reads_create_exactly_one_energy_balance(energy_game):
    with ThreadPoolExecutor(max_workers=4) as workers:
        replies=list(workers.map(lambda _:services(energy_game).energy.get(energy_game[3]),range(4)))
    assert all(r['currentEnergy']==100 for r in replies)
    with energy_game[0].app.state.database.transaction() as session:
        assert session.scalar(select(func.count()).select_from(PlayerEnergy))==1


def test_concurrent_start_replays_only_charge_once(energy_game):
    key=uuid4()
    with ThreadPoolExecutor(max_workers=4) as workers:
        replies=list(workers.map(lambda _:reserve(energy_game,key),range(4)))
    assert len({r['runId'] for r in replies})==1
    assert balance(energy_game)==95
    with energy_game[0].app.state.database.transaction() as session:
        assert session.scalar(select(func.count()).select_from(RunEnergyCharge))==1
        assert session.scalar(select(func.count()).select_from(Run))==1


def test_distinct_start_keys_share_the_active_run_without_a_second_charge(energy_game):
    first=reserve(energy_game)
    assert reserve(energy_game)['runId']==first['runId']
    assert balance(energy_game)==95


def test_insufficient_energy_rolls_back_reservation_and_request_record(energy_game):
    client,_,slot,account,_=energy_game
    assert balance(energy_game)==100
    with client.app.state.database.transaction() as session:
        session.get(PlayerEnergy,account).current_energy=4
    key=uuid4()
    with pytest.raises(ApplicationError,match='Insufficient energy'):
        reserve(energy_game,key)
    with client.app.state.database.transaction() as session:
        assert session.scalar(select(func.count()).select_from(Run))==0
        assert session.scalar(select(func.count()).select_from(RunEnergyCharge))==0
        assert session.get(IdempotencyRecord,(account,'run.start',str(key))) is None
        server=session.get(GameServer,slot.server_id)
        assert server.status=='ready' and server.current_run_id is None
        assert session.get(PlayerEnergy,account).current_energy==4


def test_failure_after_debit_rolls_back_energy_run_and_server(energy_game,monkeypatch):
    from vatican_backend.persistence.postgres.repositories.runs import RunsRepository
    def fail(*args):
        raise RuntimeError('Injected persistence failure')
    monkeypatch.setattr(RunsRepository,'record_start',fail)
    assert balance(energy_game)==100
    with pytest.raises(RuntimeError,match='Injected persistence failure'):
        reserve(energy_game)
    assert balance(energy_game)==100
    with energy_game[0].app.state.database.transaction() as session:
        assert session.scalar(select(func.count()).select_from(Run))==0
        assert session.scalar(select(func.count()).select_from(RunEnergyCharge))==0
        assert session.get(GameServer,energy_game[2].server_id).current_run_id is None


def test_worker_expires_async_assignment_and_refunds_once_under_concurrent_cleanup(energy_game):
    pending=reserve(energy_game)
    assert balance(energy_game)==95
    energy_game[4].advance(31)
    with ThreadPoolExecutor(max_workers=3) as workers:
        list(workers.map(lambda _:services(energy_game).runs.expire_pending(),range(3)))
    assert balance(energy_game)==100
    assert services(energy_game).runs.assignment_status(UUID(pending['runId']),energy_game[3])['status']=='failed'
    assert services(energy_game).runs.expire_pending()==0
    assert balance(energy_game)==100
    with energy_game[0].app.state.database.transaction() as session:
        assert session.get(RunEnergyCharge,UUID(pending['runId'])).refunded_at is not None


def test_same_request_key_after_timeout_returns_failed_run_without_a_new_debit(energy_game):
    key=uuid4()
    first=reserve(energy_game,key)
    energy_game[4].advance(31)
    replay=reserve(energy_game,key)
    assert replay['runId']==first['runId'] and replay['status']=='failed'
    assert balance(energy_game)==100


def test_late_ack_cannot_confirm_or_consume_a_refunded_assignment(energy_game):
    pending=reserve(energy_game)
    energy_game[4].advance(31)
    with pytest.raises(ApplicationError,match='deadline expired'):
        ack(energy_game,pending)
    services(energy_game).runs.expire_pending()
    with pytest.raises(ApplicationError,match='stale'):
        services(energy_game).runs.acknowledge(energy_game[2].server_id,energy_game[2].secret,
            Assignment(UUID(pending['runId']),1))
    assert balance(energy_game)==100


def test_ack_before_expiry_keeps_the_charge_and_retries_are_safe(energy_game):
    pending=reserve(energy_game)
    energy_game[4].advance(29)
    ack(energy_game,pending)
    energy_game[4].advance(2)
    assert services(energy_game).runs.expire_pending()==0
    assert ack(energy_game,pending)['replayed']
    assert balance(energy_game)==95
    with energy_game[0].app.state.database.transaction() as session:
        assert session.get(RunEnergyCharge,UUID(pending['runId'])).refunded_at is None


def test_refund_after_regeneration_can_exceed_cap_without_losing_paid_energy(energy_game):
    pending=reserve(energy_game)
    energy_game[4].advance(600)  # Ten complete ticks restore 95 to 100 before the refund.
    services(energy_game).runs.expire_pending()
    assert balance(energy_game)==105
    energy_game[4].advance(600)
    assert balance(energy_game)==105


def test_refund_uses_pinned_charge_even_if_policy_is_retired_and_configuration_removed(energy_game):
    pending=reserve(energy_game)
    with energy_game[0].app.state.database.transaction() as session:
        session.get(Ruleset,'test-energy-v1').status='RETIRED'
    services(energy_game).runs.energy=EnergyService('')
    energy_game[4].advance(31)
    services(energy_game).runs.expire_pending()
    with energy_game[0].app.state.database.transaction() as session:
        assert session.get(PlayerEnergy,energy_game[3]).current_energy==100
        assert session.get(RunEnergyCharge,UUID(pending['runId'])).refunded_at is not None


def test_new_rule_version_preserves_balance_and_partial_tick_and_cannot_be_selected_by_client(energy_game):
    client,settings,_,account,clock=energy_game
    reserve(energy_game)
    clock.advance(30)
    before=services(energy_game).energy.get(account)
    assert before['currentEnergy']==95 and before['regenProgressMs']==30_000
    configure=ConfigureEnergy(PostgresUnitOfWorkFactory(client.app.state.database))
    configure.execute(EnergyRules('energy-v2',100,120_000,2,7))
    services(energy_game).energy.service=EnergyService('energy-v2')
    changed=services(energy_game).energy.get(account)
    assert changed['currentEnergy']==95 and changed['regenProgressMs']==60_000
    assert changed['runCost']==7 and changed['rulesetId']=='energy-v2'
    response=client.get('/v1/energy?rulesetId=test-energy-v1',headers={'Authorization':'Bearer test-token'})
    assert response.json()['rulesetId']=='energy-v2'


def test_energy_policy_is_immutable_and_missing_cost_never_enables_free_starts(energy_game):
    client,_,slot,account,_=energy_game
    configure=ConfigureEnergy(PostgresUnitOfWorkFactory(client.app.state.database))
    configure.execute(EnergyRules('test-energy-v1',100,60_000,1,5))
    with pytest.raises(ApplicationError,match='immutable'):
        configure.execute(EnergyRules('test-energy-v1',100,60_000,1,0))
    services(energy_game).runs.energy=EnergyService('')
    with pytest.raises(ApplicationError,match='not configured'):
        reserve(energy_game)
    with client.app.state.database.transaction() as session:
        assert session.scalar(select(func.count()).select_from(Run))==0
        assert session.get(GameServer,slot.server_id).current_run_id is None
        assert session.get(PlayerEnergy,account) is None


def test_effect_grant_is_idempotent_nonretroactive_and_preserves_fractional_work(energy_game):
    client,_,_,account,clock=energy_game
    reserve(energy_game)
    energy=services(energy_game).energy
    clock.advance(20)
    first=energy.grant_regen_effect(account,'test:boost',Decimal('.5'),60)
    clock.advance(20)
    replay=energy.grant_regen_effect(account,'test:boost',Decimal('.5'),60)
    assert replay.id==first.id and replay.expires_at==first.expires_at
    now=energy.get(account)
    assert now['currentEnergy']==95 and now['regenProgressMs']==50_000
    clock.advance(20)
    now=energy.get(account)
    assert now['currentEnergy']==96 and now['regenProgressMs']==20_000
    with pytest.raises(ApplicationError,match='payload changed'):
        energy.grant_regen_effect(account,'test:boost',Decimal(1),60)
    with client.app.state.database.transaction() as session:
        assert session.scalar(select(func.count()).select_from(PlayerEffect))==1


def test_run_charge_enforces_owner_in_postgres(energy_game):
    pending=reserve(energy_game)
    client=energy_game[0]
    with pytest.raises(IntegrityError):
        with client.app.state.database.transaction() as session:
            session.get(RunEnergyCharge,UUID(pending['runId'])).account_id=uuid4()


def test_refund_commit_failure_preserves_original_charge_and_retry_returns_it_once(energy_game):
    pending=reserve(energy_game)
    client,_,_,account,clock=energy_game
    clock.advance(31)
    with client.app.state.database.engine.begin() as connection:
        connection.execute(text('ALTER TABLE run_energy_charges ADD CONSTRAINT test_refund_failure CHECK (refunded_at IS NULL)'))
    try:
        with pytest.raises(IntegrityError):
            services(energy_game).runs.expire_pending()
        with client.app.state.database.transaction() as session:
            assert session.get(PlayerEnergy,account).current_energy==95
            assert session.get(Run,UUID(pending['runId'])).status=='pending'
            assert session.get(RunEnergyCharge,UUID(pending['runId'])).refunded_at is None
    finally:
        with client.app.state.database.engine.begin() as connection:
            connection.execute(text('ALTER TABLE run_energy_charges DROP CONSTRAINT test_refund_failure'))
    services(energy_game).runs.expire_pending()
    assert balance(energy_game)==100
    assert services(energy_game).runs.expire_pending()==0


def test_checkpoint_recovery_never_refunds_the_charge_of_a_started_run(energy_game):
    import hashlib,json
    pending=reserve(energy_game)
    ack(energy_game,pending)
    client,_,slot,account,clock=energy_game
    payload=json.dumps({'schemaVersion':4,'runId':pending['runId'],'simulationTick':1,
                        'contentReleaseId':'sha256-test-release'})
    runs=services(energy_game).runs
    runs.publish_checkpoint(slot.server_id,slot.secret,Checkpoint(UUID(pending['runId']),1,4,1,payload,
                                                                hashlib.sha256(payload.encode()).hexdigest()))
    clock.advance(20)
    recovered=runs.recover(slot.server_id,slot.secret,ServerRegistration('sha256-test-release',3,'udp-loopback'))
    assert recovered['generation']==2
    clock.advance(31)
    assert runs.expire_pending()==0
    assert balance(energy_game)==95
    with client.app.state.database.transaction() as session:
        assert session.get(Run,UUID(pending['runId'])).status=='pending'
        assert session.get(RunEnergyCharge,UUID(pending['runId'])).refunded_at is None


def test_missing_server_and_inventory_outage_never_consume_energy(energy_game):
    client,_,slot,account,_=energy_game
    assert balance(energy_game)==100
    with client.app.state.database.transaction() as session:
        session.get(GameServer,slot.server_id).status='unavailable'
    with pytest.raises(ApplicationError,match='No live game process'):
        reserve(energy_game)
    assert balance(energy_game)==100
    class Down:
        def snapshot(self, account_id):
            assert client.app.state.database.engine.pool.checkedout()==0
            raise ApplicationError(503,'Inventory unavailable')
    services(energy_game).runs.inventory=Down()
    with pytest.raises(ApplicationError,match='Inventory unavailable'):
        reserve(energy_game)
    assert balance(energy_game)==100


def test_current_energy_is_server_owned_and_ruleset_input_cannot_select_a_free_config(energy_game):
    client,_,_,account,_=energy_game
    headers={'Authorization':'Bearer test-token'}
    response=client.post('/v1/runs',headers=headers,json={'requestKey':str(uuid4()),'energyCost':0,'currentEnergy':100000})
    assert response.status_code==422
    assert balance(energy_game)==100


def test_legacy_energy_config_requires_an_explicit_cost_and_keeps_existing_balance(energy_game):
    client,_,_,account,_=energy_game
    with client.app.state.database.transaction() as session:
        session.add(Ruleset(id='legacy-energy',code='legacy-energy',status='PUBLISHED'))
        session.flush()
        session.add(EnergyConfig(ruleset_id='legacy-energy',max_energy=100,regen_interval_ms=60_000,energy_per_tick=1))
    services(energy_game).energy.service=EnergyService('legacy-energy')
    with pytest.raises(ApplicationError,match='unavailable'):
        services(energy_game).energy.get(account)
    configure=ConfigureEnergy(PostgresUnitOfWorkFactory(client.app.state.database))
    with pytest.raises(ApplicationError,match='immutable'):
        configure.execute(EnergyRules('legacy-energy',200,60_000,1,5))
    configure.execute(EnergyRules('legacy-energy',100,60_000,1,8))
    assert services(energy_game).energy.get(account)['runCost']==8


def test_energy_failure_codes_are_stable_for_the_client(energy_game):
    client,_,_,account,_=energy_game
    headers={'Authorization':'Bearer test-token'}
    assert balance(energy_game)==100
    with client.app.state.database.transaction() as session:
        session.get(PlayerEnergy,account).current_energy=0
    denied=client.post('/v1/runs',headers=headers,json={'requestKey':str(uuid4())})
    assert denied.status_code==409
    assert denied.json()['code']=='InsufficientEnergy'
    services(energy_game).energy.service=EnergyService('')
    unavailable=client.get('/v1/energy',headers=headers)
    assert unavailable.status_code==503
    assert unavailable.json()['code']=='EnergyRulesUnavailable'
