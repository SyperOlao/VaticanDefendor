"""Integrity checks for the current ORM relationships."""
from datetime import datetime, timedelta, timezone
from uuid import uuid4

import pytest
from sqlalchemy.exc import IntegrityError

from .support import player_for
from vatican_backend.application.errors import ApplicationError
from vatican_backend.persistence.postgres.tables import (
    IdempotencyRecord,
    PlayerXPTransaction, Ruleset, RulesetLimit, Run, RunContext,
    RunResultSubmission, RunValidationAudit, RunXPBreakdown,
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


def test_validation_audit_and_limits_have_real_parents(storage):
    database, _ = storage
    _, _, run_id = local_run(database)
    with pytest.raises(IntegrityError):
        with database.transaction() as session:
            session.add(RunValidationAudit(run_id=uuid4(), ruleset_id="rules-a", validator_version="v1", status="REVIEW"))
    # Dedicated audits reference a real run without requiring a LOCAL submission.
    with database.transaction() as session:
        session.add(RunValidationAudit(run_id=run_id, ruleset_id="rules-a", validator_version="dedicated.v2", status="REVIEW"))
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


