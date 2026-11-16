"""Integrity checks for the current ORM relationships."""
from datetime import datetime, timedelta, timezone
from uuid import uuid4

import pytest
from sqlalchemy.exc import IntegrityError

from support import payment_order, player_for
from vatican_backend.application.errors import ApplicationError
from vatican_backend.persistence.postgres.tables import (
    Currency, EconomyTransaction, IdempotencyRecord,
    OutboxMessage, OwnedWeapon, PaymentOrder, PaymentProvider, PlayerLevelDefinition,
    PlayerLevelRewardClaim, PlayerXPTransaction, PrestigeRewardRule,
    PurchaseTransaction, RewardBundle, Ruleset, RulesetLimit, Run, RunContext,
    RunRewardGrant, RunResultSubmission, RunValidationAudit, RunXPBreakdown,
    WeaponConfiguration, WeaponLoadout,
)


def local_run(database):
    owner, other, run_id = uuid4(), uuid4(), uuid4()
    with database.transaction() as session:
        session.add_all([Ruleset(id="rules-a", code="rules-a"),
                         Ruleset(id="rules-b", code="rules-b")])
        session.flush()
        session.add(Run(id=run_id, account_id=owner, server_id=None, simulation_mode="LOCAL",
                         ruleset_id="rules-a", content_release_id="local-content", profile_version=1,
                         profile_display_name="Player", generation=0, status="ready"))
    return owner, other, run_id


def test_local_run_context_pins_owner_and_ruleset(storage):
    database, _ = storage
    owner, other, run_id = local_run(database)
    context = dict(run_id=run_id, account_id=owner, session_id=uuid4(), client_request_key=uuid4(),
                   map_id="village", difficulty="normal", ruleset_id="rules-a", energy_cost=5,
                   submission_deadline=datetime.now(timezone.utc) + timedelta(days=1))
    for change in ({"account_id": other}, {"ruleset_id": "rules-b"}):
        with pytest.raises(IntegrityError):
            with database.transaction() as session:
                session.add(RunContext(**{**context, **change}))
    with database.transaction() as session:
        session.add(RunContext(**context))
        assert session.get(Run, run_id).server_id is None


def test_dedicated_assignment_does_not_discard_a_local_run(backend):
    client, _, _ = backend
    from uuid import UUID
    player = player_for(client)
    run_id = uuid4()
    with client.app.state.database.transaction() as session:
        session.add(Ruleset(id="local-rules", code="local-rules"))
        session.flush()
        session.add(Run(id=run_id, account_id=UUID(player["accountId"]), simulation_mode="LOCAL",
            ruleset_id="local-rules", content_release_id="local-content", profile_version=1,
            profile_display_name="Guest", generation=0, status="ready"))
    with pytest.raises(ApplicationError) as failure:
        client.app.state.use_cases.runs.assign(UUID(player["accountId"]))
    assert failure.value.status_code == 409
    with client.app.state.database.transaction() as session:
        assert session.get(Run, run_id).status == "ready"


def test_xp_breakdown_and_ledger_cannot_change_run_ruleset_or_owner(storage):
    database, _ = storage
    owner, other, run_id = local_run(database)
    breakdown = dict(run_id=run_id, ruleset_id="rules-a", hero_kill_xp=10, tower_kill_xp=5,
                     tycoon_xp=2, victory_xp=3, total_xp=20)
    with pytest.raises(IntegrityError):
        with database.transaction() as session:
            session.add(RunXPBreakdown(**{**breakdown, "ruleset_id": "rules-b"}))
    with database.transaction() as session:
        session.add(RunXPBreakdown(**breakdown))
    with pytest.raises(IntegrityError):
        with database.transaction() as session:
            session.add(PlayerXPTransaction(account_id=other, run_id=run_id, xp_delta=20))
    with database.transaction() as session:
        session.add(PlayerXPTransaction(account_id=owner, run_id=run_id, xp_delta=20))


def test_run_reward_ledger_belongs_to_the_same_account(storage):
    database, _ = storage
    owner, other, run_id = local_run(database)
    wrong_tx, correct_tx = uuid4(), uuid4()
    with database.transaction() as session:
        session.add(Currency(code="PRESTIGE"))
        session.add(PrestigeRewardRule(ruleset_id="rules-a", church_level=5, reward=25))
        session.flush()
        for account_id, transaction_id in [(other, wrong_tx), (owner, correct_tx)]:
            session.add(EconomyTransaction(id=transaction_id, account_id=account_id, currency_code="PRESTIGE",
                delta=25, balance_after=25, reason="VICTORY", source_type="RUN_REWARD", source_id=run_id))
    with pytest.raises(IntegrityError):
        with database.transaction() as session:
            session.add(RunRewardGrant(run_id=run_id, account_id=owner, transaction_id=wrong_tx,
                                       ruleset_id="rules-a", church_level=5))
    with database.transaction() as session:
        session.add(RunRewardGrant(run_id=run_id, account_id=owner, transaction_id=correct_tx,
                                   ruleset_id="rules-a", church_level=5))


def test_weapon_loadout_cannot_reference_another_owners_instance(storage):
    database, _ = storage
    owner, other, _ = local_run(database)
    instance = uuid4()
    with database.transaction() as session:
        session.add(WeaponConfiguration(configuration_id="audit.weapon", configuration_json={"Id": "audit.weapon"}))
        session.flush()
        session.add(OwnedWeapon(instance_id=instance, account_id=owner, configuration_id="audit.weapon", grant_key="audit"))
    with pytest.raises(IntegrityError):
        with database.transaction() as session:
            session.add(WeaponLoadout(account_id=other, slot=0, instance_id=instance))
    with pytest.raises(IntegrityError):
        with database.transaction() as session:
            session.add(OwnedWeapon(instance_id=uuid4(), account_id=owner, configuration_id="missing", grant_key="missing"))


def test_payment_order_provider_environment_and_outbox_are_linked(storage):
    database, _ = storage
    order_id = payment_order(database)
    with database.transaction() as session:
        session.add(PaymentProvider(id=2, code="OTHER", provider_kind="STORE"))
        session.get(PaymentOrder, order_id).status = "UNKNOWN"
    for provider_id, environment in [(2, "TEST"), (1, "PRODUCTION")]:
        with pytest.raises(IntegrityError):
            with database.transaction() as session:
                session.add(PurchaseTransaction(order_id=order_id, provider_id=provider_id,
                    environment=environment, external_transaction_id="external", verification_status="VERIFIED"))
    transaction_id = uuid4()
    with database.transaction() as session:
        session.add(PurchaseTransaction(id=transaction_id, order_id=order_id, provider_id=1,
            environment="TEST", external_transaction_id="external", verification_status="VERIFIED"))
    with pytest.raises(IntegrityError):
        with database.transaction() as session:
            session.add(OutboxMessage(destination="payments", event_name="purchase_verified", schema_version=1,
                aggregate_id=uuid4(), payment_transaction_id=transaction_id, payload={}))
    with database.transaction() as session:
        session.add(OutboxMessage(destination="payments", event_name="purchase_verified", schema_version=1,
            aggregate_id=transaction_id, payment_transaction_id=transaction_id, payload={}))


def test_level_claim_requires_its_actual_level_definition_and_bundle(storage):
    database, _ = storage
    owner, _, _ = local_run(database)
    bundle_id, wrong_bundle = uuid4(), uuid4()
    with database.transaction() as session:
        session.add_all([RewardBundle(id=bundle_id, code="gift", version=1),
                         RewardBundle(id=wrong_bundle, code="other", version=1)])
        session.flush()
        session.add(PlayerLevelDefinition(ruleset_id="rules-a", level=2, total_xp_required=100, reward_bundle_id=bundle_id))
    with pytest.raises(IntegrityError):
        with database.transaction() as session:
            session.add(PlayerLevelRewardClaim(account_id=owner, ruleset_id="rules-a", rewarded_level=2, reward_bundle_id=wrong_bundle))
    with database.transaction() as session:
        session.add(PlayerLevelRewardClaim(account_id=owner, ruleset_id="rules-a", rewarded_level=2, reward_bundle_id=bundle_id))


def test_validation_audit_and_limits_have_real_parents(storage):
    database, _ = storage
    _, _, run_id = local_run(database)
    with pytest.raises(IntegrityError):
        with database.transaction() as session:
            session.add(RunValidationAudit(run_id=run_id, ruleset_id="rules-a", validator_version="v1", status="REVIEW"))
    with database.transaction() as session:
        session.add(RunResultSubmission(run_id=run_id, payload_hash=b"x" * 32, schema_version=1,
                                        snapshot={}, validation_status="REVIEW", validator_version="v1"))
        session.flush()
        session.add(RunValidationAudit(run_id=run_id, ruleset_id="rules-a", validator_version="v1", status="REVIEW", reasons=["kills_limit"]))
        session.add(RulesetLimit(ruleset_id="rules-a", rule_key="kills", maximum=100))
    with pytest.raises(IntegrityError):
        with database.transaction() as session:
            session.add(RulesetLimit(ruleset_id="rules-a", rule_key="duration", minimum=100, maximum=1))


def test_idempotency_scope_is_unique(storage):
    database, _ = storage
    owner, _, _ = local_run(database)
    with database.transaction() as session:
        for operation in ["run.start", "purchase"]:
            session.add(IdempotencyRecord(account_id=owner, operation=operation, request_key="request-1", payload_hash=b"h" * 32))
    with pytest.raises(IntegrityError):
        with database.transaction() as session:
            session.add(IdempotencyRecord(account_id=owner, operation="run.start", request_key="request-1", payload_hash=b"h" * 32))


