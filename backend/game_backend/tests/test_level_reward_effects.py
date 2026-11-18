from concurrent.futures import ThreadPoolExecutor
from copy import deepcopy
from datetime import datetime, timedelta, timezone
from decimal import Decimal
from uuid import UUID, uuid4

import pytest
from sqlalchemy import func, select, text
from sqlalchemy.exc import IntegrityError

from vatican_backend.application.energy import EnergyService
from vatican_backend.application.errors import ApplicationError
from vatican_backend.application.level_rewards import PublishRewardBundle
from vatican_backend.application.messaging import DeliverRewards
from vatican_backend.domain.effects import EffectType, GiftEffect
from vatican_backend.domain.level_rewards import RewardPlan, LEVEL_REWARD_EVENT
from vatican_backend.migrate import migrate
from vatican_backend.persistence.postgres.tables import (LevelRewardGrant, OutboxMessage, PlayerEffect,
    PlayerEnergy, PlayerLevelRewardClaim, RewardBundleItem)
from vatican_backend.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory
from vatican_rating.application.use_cases import RatingUseCases
from vatican_rating.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as RatingUow
from vatican_wallet.application.use_cases import WalletUseCases
from vatican_wallet.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as WalletUow
from .test_energy_lifecycle import FakeClock
from .test_progression_lifecycle import xp_game, start, apply


EFFECT = {'modifierValue': '0.500000', 'durationSeconds': 60}


def effect_line(index=1):
    return {'itemIndex': index, 'rewardType': 'EFFECT', 'rewardRef': EffectType.ENERGY_REGEN_RATE_BONUS,
            'quantity': 1, 'effect': dict(EFFECT)}


def plan():
    return {'schemaVersion': 1, 'bundleId': str(uuid4()), 'items': [effect_line()]}


@pytest.mark.parametrize('patch', [
    {'quantity': 2}, {'quantity': True}, {'rewardRef': 'UNKNOWN'}, {'effect': None},
    {'effect': {'modifierValue': 'NaN', 'durationSeconds': 60}},
    {'effect': {'modifierValue': 'Infinity', 'durationSeconds': 60}},
    {'effect': {'modifierValue': '0', 'durationSeconds': 60}},
    {'effect': {'modifierValue': '-0.5', 'durationSeconds': 60}},
    {'effect': {'modifierValue': '0.0000001', 'durationSeconds': 60}},
    {'effect': {'modifierValue': '1000000000000', 'durationSeconds': 60}},
    {'effect': {'modifierValue': 0.5, 'durationSeconds': 60}},
    {'effect': {'modifierValue': '0.5', 'durationSeconds': 0}},
    {'effect': {'modifierValue': '0.5', 'durationSeconds': True}},
    {'effect': {'modifierValue': '0.5', 'durationSeconds': 31_536_001}},
    {'effect': {'modifierValue': '0.5', 'durationSeconds': 60, 'startsAt': 'client-time'}},
    {'rewardType': 'ENERGY', 'rewardRef': 'ENERGY'},
])
def test_effect_plan_rejects_invalid_parameters(patch):
    value = plan()
    value['items'][0].update(patch)
    with pytest.raises(ValueError):
        RewardPlan.parse(value)


def test_effect_parameters_are_canonical_and_required():
    value = plan()
    value['items'][0]['effect']['modifierValue'] = '.5'
    assert RewardPlan.parse(value).snapshot()['items'][0] == effect_line()
    del value['items'][0]['effect']
    with pytest.raises(ValueError):
        RewardPlan.parse(value)
    with pytest.raises(ValueError):
        GiftEffect(Decimal('0.5000001'), 60)  # Publication must not round an invalid programmatic parameter.


@pytest.fixture
def effect_game(xp_game, wallet_storage, rating_storage, analytics_actor):
    client, _, account, rules = xp_game
    db = client.app.state.database
    clock = FakeClock(datetime.now(timezone.utc))
    cases = client.app.state.use_cases
    for case in (cases.runs, cases.energy, cases.level_rewards):
        case.clock = clock
    bundle = rules.levels[1].reward_bundle_id
    with db.transaction() as session:
        session.add(RewardBundleItem(bundle_id=bundle, item_index=1, reward_type='EFFECT',
            reward_ref=EffectType.ENERGY_REGEN_RATE_BONUS, quantity=1, effect_config=EFFECT))
    result = apply(xp_game, start(xp_game))
    claim_id = UUID(result['progression']['unlockedRewards'][0]['claimId'])
    wallet = WalletUseCases(WalletUow(wallet_storage[0]))
    return {'client': client, 'db': db, 'account': account, 'bundle': bundle, 'claim': claim_id,
            'clock': clock, 'cases': cases, 'wallet': wallet, 'rating': RatingUseCases(RatingUow(rating_storage[0])), 'analytics': analytics_actor, 'uow': PostgresUnitOfWorkFactory(db)}


def claim(w):
    return w['cases'].level_rewards.claim(w['account'], w['claim'])


def get(w):
    return w['cases'].level_rewards.get(w['account'], w['claim'])


def effects(w):
    with w['db'].transaction() as session:
        return [(e.id, e.source_key, e.modifier_value, e.starts_at, e.expires_at)
                for e in session.scalars(select(PlayerEffect).order_by(PlayerEffect.source_key))]


def drain(w, down=False):
    class Gateway:
        def deliver(self, message):
            assert w['db'].engine.pool.checkedout() == 0
            if message['destination'] in ('rating','analytics'):
                return w[message['destination']].receive(message['id'],message['event_name'],message['schema_version'],message['payload'])['receipt']
            if message['event_name'] != LEVEL_REWARD_EVENT:
                return None
            assert message['destination'] == 'wallet'  # EFFECT never enters the remote outbox.
            if down:
                raise ApplicationError(503, 'Wallet unavailable')
            return w['wallet'].receive(message['id'], message['event_name'], 1, message['payload'])['receipt']
    worker = DeliverRewards(w['uow'], Gateway())
    for _ in range(10):
        if not worker.once():
            return
    raise AssertionError('Worker did not quiesce')


def test_effect_is_nonretroactive_and_replay_after_expiry_never_extends_it(effect_game):
    w = effect_game
    w['clock'].advance(20)
    first = claim(w)
    line = first['lines'][1]
    assert first['status'] == 'PENDING' and first['deliveryStatus'] == 'PARTIAL'
    assert line['status'] == 'COMPLETED' and line['messageId'] is None and line['effect'] == EFFECT
    receipt = line['receipt']
    assert receipt['startsAt'] == w['clock'].at.isoformat()
    assert datetime.fromisoformat(receipt['expiresAt']) == w['clock'].at + timedelta(seconds=60)
    # The first twenty seconds use the base speed, the next twenty use +50%.
    w['clock'].advance(20)
    energy = w['cases'].energy.get(w['account'])
    assert (energy['currentEnergy'], energy['regenProgressMs']) == (95, 50_000)
    original = effects(w)
    w['clock'].advance(100)
    assert claim(w)['lines'][1] == line
    assert effects(w) == original
    energy = w['cases'].energy.get(w['account'])
    assert (energy['currentEnergy'], energy['regenProgressMs']) == (97, 50_000)
    drain(w)
    assert get(w)['status'] == 'CLAIMED'
    assert claim(w)['lines'][1] == line and effects(w) == original


def test_concurrent_claims_apply_one_effect_and_one_original_window(effect_game):
    w = effect_game
    with ThreadPoolExecutor(max_workers=6) as workers:
        replies = list(workers.map(lambda _: claim(w), range(6)))
    assert sum(not r['replayed'] for r in replies) == 1
    assert len({r['lines'][1]['receipt']['effectId'] for r in replies}) == 1
    assert len(effects(w)) == 1


def test_partial_wallet_failure_and_manual_redrive_do_not_reapply_expired_effect(effect_game):
    w = effect_game
    line = claim(w)['lines'][1]
    drain(w, down=True)
    assert get(w)['deliveryStatus'] == 'PARTIAL'
    with w['db'].transaction() as session:
        message = session.scalar(select(OutboxMessage).where(OutboxMessage.aggregate_id == w['claim']))
        message.attempts = 19
        message.available_at = datetime.now(timezone.utc) - timedelta(seconds=1)
        identity = message.id
    drain(w, down=True)
    assert get(w)['status'] == 'REVIEW'
    w['clock'].advance(120)
    assert claim(w)['lines'][1] == line
    assert w['cases'].rewards.requeue(identity)
    drain(w)
    assert get(w)['status'] == 'CLAIMED'
    assert get(w)['lines'][1] == line and len(effects(w)) == 1


def test_failure_after_effect_insertion_rolls_back_effect_energy_and_remote_jobs(effect_game):
    w = effect_game
    w['clock'].advance(20)
    with w['db'].engine.begin() as connection:
        connection.execute(text("ALTER TABLE level_reward_grants ADD CONSTRAINT injected_effect_failure CHECK (reward_type <> 'EFFECT')"))
    try:
        with pytest.raises(IntegrityError):
            claim(w)
        assert effects(w) == [] and get(w)['status'] == 'AVAILABLE'
        with w['db'].transaction() as session:
            assert session.get(PlayerEnergy, w['account']).regen_progress_ms == 0
            assert session.scalar(select(func.count()).select_from(LevelRewardGrant)) == 0
            assert session.scalar(select(func.count()).select_from(OutboxMessage).where(OutboxMessage.aggregate_id == w['claim'])) == 0
    finally:
        with w['db'].engine.begin() as connection:
            connection.execute(text('ALTER TABLE level_reward_grants DROP CONSTRAINT injected_effect_failure'))
    claim(w)
    assert len(effects(w)) == 1


def test_frozen_parameters_survive_bundle_edit_and_unconfigured_energy_rejects_atomically(effect_game):
    w = effect_game
    with w['db'].transaction() as session:
        session.get(RewardBundleItem, (w['bundle'], 1)).effect_config = {'modifierValue': '9.000000', 'durationSeconds': 3600}
    original_service = w['cases'].level_rewards.energy
    w['cases'].level_rewards.energy = EnergyService('missing')
    with pytest.raises(ApplicationError):
        claim(w)
    assert get(w)['status'] == 'AVAILABLE' and effects(w) == []
    w['cases'].level_rewards.energy = original_service
    assert claim(w)['lines'][1]['effect'] == EFFECT
    assert effects(w)[0][2] == Decimal('.5')


def test_effect_only_bundle_is_published_immutably_and_completes_synchronously(effect_game):
    w = effect_game
    value = plan()
    publish = PublishRewardBundle(w['uow'])
    publish.execute('effect-only', 1, RewardPlan.parse(value))
    publish.execute('effect-only', 1, RewardPlan.parse(value))
    changed = deepcopy(value)
    changed['items'][0]['effect']['durationSeconds'] = 120
    with pytest.raises(ApplicationError, match='immutable'):
        publish.execute('effect-only', 1, RewardPlan.parse(changed))
    # Keep the already unlocked claim's own bundle FK and freeze an effect-only plan.
    with w['db'].transaction() as session:
        row = session.get(PlayerLevelRewardClaim, w['claim'])
        row.reward_snapshot = {**row.reward_snapshot, 'items': [effect_line()]}
    response = w['client'].post(f"/v1/level-rewards/{w['claim']}/claim", headers={'Authorization': 'Bearer test-token'})
    assert response.status_code == 200 and response.json()['status'] == 'CLAIMED'
    assert response.json()['analyticsStatus'] == 'PENDING'
    assert claim(w)['replayed'] and len(effects(w)) == 1


def test_source_guard_reuses_same_effect_and_rejects_changed_parameters(effect_game):
    w = effect_game
    source = f"level-reward:{w['claim']}:1"
    with w['uow']() as work:
        work.profiles.get(w['account'], lock=True)
        original = w['cases'].level_rewards.energy.grant_regen_effect(work, w['account'], source, '.5', 60, w['clock'].at)
    w['clock'].advance(120)
    result = claim(w)
    assert result['lines'][1]['receipt']['effectId'] == str(original.id)
    assert len(effects(w)) == 1
    with w['uow']() as work:
        work.profiles.get(w['account'], lock=True)
        with pytest.raises(ApplicationError, match='payload changed'):
            w['cases'].level_rewards.energy.grant_regen_effect(work, w['account'], source, '1', 60, w['clock'].at)
    with pytest.raises(ApplicationError, match='Reserved'):
        w['cases'].energy.grant_regen_effect(w['account'], source, '.5', 60)


def test_two_effect_lines_stack_once_and_clock_rollback_cannot_rewind_their_window(effect_game):
    w = effect_game
    with w['db'].transaction() as session:
        session.add(RewardBundleItem(bundle_id=w['bundle'], item_index=2, reward_type='EFFECT',
            reward_ref=EffectType.ENERGY_REGEN_RATE_BONUS, quantity=1, effect_config=EFFECT))
        row = session.get(PlayerLevelRewardClaim, w['claim'])
        row.reward_snapshot = {**row.reward_snapshot, 'items': [effect_line(1), effect_line(2)]}
    original_time = w['clock'].at
    w['clock'].advance(-5)
    result = claim(w)
    assert result['status'] == 'CLAIMED'
    assert len(effects(w)) == 2
    assert all(e[3] == original_time for e in effects(w))
    w['clock'].advance(35)
    assert w['cases'].energy.get(w['account'])['currentEnergy'] == 96  # Base + .5 + .5, thirty seconds.
    original = effects(w)
    assert claim(w)['replayed'] and effects(w) == original


def test_invalid_effect_rejects_whole_gift_and_client_cannot_override_parameters(effect_game):
    w = effect_game
    endpoint = f"/v1/level-rewards/{w['claim']}/claim"
    response = w['client'].post(endpoint, headers={'Authorization': 'Bearer test-token'},
                              json={'effect': {'modifierValue': '100', 'durationSeconds': 31536000}})
    assert response.status_code == 422 and effects(w) == []
    with w['db'].transaction() as session:
        row = session.get(PlayerLevelRewardClaim, w['claim'])
        snapshot = deepcopy(row.reward_snapshot)
        snapshot['items'][1]['effect']['durationSeconds'] = -1
        row.reward_snapshot = snapshot
    with pytest.raises(ApplicationError) as error:
        claim(w)
    assert error.value.code == 'InvalidRewardBundle'
    assert get(w)['status'] == 'AVAILABLE' and effects(w) == []
    with w['db'].transaction() as session:
        assert session.scalar(select(func.count()).select_from(LevelRewardGrant)) == 0
        assert session.scalar(select(func.count()).select_from(OutboxMessage).where(OutboxMessage.aggregate_id == w['claim'])) == 0
