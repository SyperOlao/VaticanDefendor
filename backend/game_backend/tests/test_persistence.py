"""Database guarantees for the new schema, exercised against PostgreSQL."""
import os
from pathlib import Path
import subprocess
import sys
from concurrent.futures import ThreadPoolExecutor
from datetime import timedelta
from uuid import uuid4

import pytest
from sqlalchemy import func, select, text
from sqlalchemy.exc import IntegrityError

from support import payment_order
from vatican_backend.migrate import migrate, SCHEMA_REVISION
from vatican_backend.persistence.postgres.tables import (
    ContentRelease, Currency, EconomyTransaction,
    EnergyConfig, EntitlementGrant, LevelRewardGrant,
    PlayerEnergy, PlayerLevelRewardClaim, Profile, ProviderNotification,
    PurchaseTransaction, RewardBundle, RewardBundleItem, Ruleset, PlayerLevelDefinition,
)


def test_transactions_and_repeated_initialization(storage):
    database, url = storage
    account_id = uuid4()
    with database.transaction() as session:
        session.flush()
        session.add(Profile(account_id=account_id, version=7, display_name="Existing", permanent_currency=490))
    with pytest.raises(RuntimeError):
        with database.transaction() as session:
            profile = session.get(Profile, account_id)
            profile.permanent_currency = 0
            session.flush()
            raise RuntimeError("abort use case")
    migrate(url)
    migrate(url)
    with database.transaction() as session:
        assert session.get(Profile, account_id).permanent_currency == 490
        assert session.scalar(text("SELECT version_num FROM alembic_version")) == SCHEMA_REVISION


def test_backend_models_do_not_import_analytics_or_clickhouse():
    source = """
import sys
from vatican_backend.persistence.postgres import Base
assert not any(name.startswith(('vatican_analytics', 'clickhouse_sqlalchemy')) for name in sys.modules)
"""
    directory = Path(__file__).parents[1]
    subprocess.run([sys.executable, "-c", source], cwd=directory,
                   env={**os.environ, "PYTHONPATH": str(directory)}, check=True)


def test_energy_allows_paid_overcap_and_rejects_negative_energy(storage):
    database, _ = storage
    account_id = uuid4()
    with database.transaction() as session:
        session.add(ContentRelease(id="rules-v1", schema_version=1, payload_json="{}", payload_sha256="0" * 64))
        session.flush()
        session.add(Ruleset(id="rules-v1", code="rules-v1", content_release_id="rules-v1"))
        session.flush()
        session.add(EnergyConfig(ruleset_id="rules-v1", max_energy=100, regen_interval_ms=60_000, energy_per_tick=1))
        session.flush()
        session.add(PlayerEnergy(account_id=account_id, ruleset_id="rules-v1", current_energy=200))
    with pytest.raises(IntegrityError):
        with database.transaction() as session:
            session.get(PlayerEnergy, account_id).current_energy = -1
    with database.transaction() as session:
        energy = session.get(PlayerEnergy, account_id)
        assert energy.current_energy == 200
        assert energy.last_calculated_at.utcoffset() == timedelta(0)


def test_ledger_deduplicates_concurrent_delivery(storage):
    database, _ = storage
    account_id, source_id = uuid4(), uuid4()
    with database.transaction() as session:
        session.add(Currency(code="GEMS"))

    def deliver(_):
        try:
            with database.transaction() as session:
                session.add(EconomyTransaction(account_id=account_id, currency_code="GEMS", delta=10,
                                               balance_after=10, reason="PURCHASE", source_type="PAYMENT",
                                               source_id=source_id))
            return "created"
        except IntegrityError:
            return "duplicate"

    with ThreadPoolExecutor(max_workers=2) as workers:
        assert sorted(workers.map(deliver, range(2))) == ["created", "duplicate"]
    with database.transaction() as session:
        assert session.scalar(select(func.count()).select_from(EconomyTransaction)) == 1


def test_payment_transaction_and_grant_have_distinct_idempotency_keys(storage):
    database, _ = storage
    order_id = payment_order(database)
    transaction_id = uuid4()
    with database.transaction() as session:
        session.add(PurchaseTransaction(id=transaction_id, order_id=order_id, provider_id=1, environment="TEST",
                                         external_transaction_id="store-1", verification_status="VERIFIED"))
    with pytest.raises(IntegrityError):
        with database.transaction() as session:
            session.add(PurchaseTransaction(order_id=order_id, provider_id=1, environment="TEST",
                                             external_transaction_id="store-1", verification_status="VERIFIED"))
    # Another store transaction for the same consumable product is legitimate.
    with database.transaction() as session:
        session.add(PurchaseTransaction(order_id=order_id, provider_id=1, environment="TEST",
                                         external_transaction_id="store-2", verification_status="VERIFIED"))
        session.add(EntitlementGrant(transaction_id=transaction_id, reward_line_index=0,
                                     reward_type="CURRENCY", reward_ref="GEMS", amount=100))
    with pytest.raises(IntegrityError):
        with database.transaction() as session:
            session.add(EntitlementGrant(transaction_id=transaction_id, reward_line_index=0,
                                         reward_type="CURRENCY", reward_ref="GEMS", amount=100))


def test_duplicate_notification_is_scoped_to_provider_environment(storage):
    database, _ = storage
    payment_order(database)
    with database.transaction() as session:
        session.add(ProviderNotification(provider_id=1, environment="TEST", external_event_id="event-1", payload_hash=b"x" * 32))
        session.add(ProviderNotification(provider_id=1, environment="PRODUCTION", external_event_id="event-1", payload_hash=b"x" * 32))
    with pytest.raises(IntegrityError):
        with database.transaction() as session:
            session.add(ProviderNotification(provider_id=1, environment="TEST", external_event_id="event-1", payload_hash=b"x" * 32))


def test_level_gift_cannot_be_claimed_twice_or_granted_from_another_bundle(storage):
    database, _ = storage
    account_id, bundle_id, other_bundle, claim_id = (uuid4() for _ in range(4))
    with database.transaction() as session:
        session.add_all([RewardBundle(id=bundle_id, code="level", version=1),
                         RewardBundle(id=other_bundle, code="other", version=1)])
        session.add(Ruleset(id="level-rules", code="level-rules"))
        session.flush()
        session.add(PlayerLevelDefinition(ruleset_id="level-rules", level=2, total_xp_required=100, reward_bundle_id=bundle_id))
        session.flush()
        session.add(RewardBundleItem(bundle_id=bundle_id, item_index=0, reward_type="CURRENCY", reward_ref="GEMS", quantity=10))
        session.add(RewardBundleItem(bundle_id=other_bundle, item_index=0, reward_type="CURRENCY", reward_ref="GEMS", quantity=1000))
        session.add(PlayerLevelRewardClaim(id=claim_id, account_id=account_id, ruleset_id="level-rules", rewarded_level=2, reward_bundle_id=bundle_id))
    with pytest.raises(IntegrityError):
        with database.transaction() as session:
            session.add(PlayerLevelRewardClaim(account_id=account_id, ruleset_id="level-rules", rewarded_level=2, reward_bundle_id=bundle_id))
    with pytest.raises(IntegrityError):
        with database.transaction() as session:
            session.add(LevelRewardGrant(claim_id=claim_id, bundle_id=other_bundle, item_index=0))
    with database.transaction() as session:
        session.add(LevelRewardGrant(claim_id=claim_id, bundle_id=bundle_id, item_index=0))
    with pytest.raises(IntegrityError):
        with database.transaction() as session:
            session.add(LevelRewardGrant(claim_id=claim_id, bundle_id=bundle_id, item_index=0))
