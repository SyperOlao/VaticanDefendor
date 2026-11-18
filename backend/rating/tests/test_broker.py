from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone
import json
from types import SimpleNamespace
from uuid import UUID, uuid4

import pytest
from sqlalchemy import func, select, text

from vatican_rating.application.broker import AcceptBrokerDelivery, BrokerOperations, ProcessBrokerCommands
from vatican_rating.application.use_cases import RatingUseCases
from vatican_rating.domain.broker import Command, Destination, InboxStatus
from vatican_rating.persistence.postgres.tables import BrokerInbox, BrokerRejection, OutboxMessage, RunScoreReceipt
from vatican_rating.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory


@pytest.fixture
def world(rating_storage):
    db, url = rating_storage
    uow = PostgresUnitOfWorkFactory(db)
    api = RatingUseCases(uow)
    return SimpleNamespace(db=db, url=url, uow=uow, api=api, accept=AcceptBrokerDelivery(uow),
                           process=ProcessBrokerCommands(uow, api), operations=BrokerOperations(uow))


def envelope(**patch):
    return {'messageId': str(uuid4()), 'eventName': 'run.rating', 'schemaVersion': 1,
            'payload': {'accountId': str(uuid4()), 'runId': str(uuid4()), 'boardCode': 'run-xp',
                        'rulesetId': 'xp-v1', 'contentReleaseId': 'release-v1', 'points': 80, **patch}}


def accept(world, body):
    world.accept.execute(json.dumps(body).encode(), body['messageId'], 'application/json', 'utf-8')


def expire(world):
    with world.db.transaction() as session:
        for row in session.scalars(select(BrokerInbox).where(BrokerInbox.status == 'PENDING')):
            row.available_at = datetime.now(timezone.utc) - timedelta(seconds=1)
        for row in session.scalars(select(OutboxMessage).where(OutboxMessage.published_at.is_(None))):
            row.available_at = datetime.now(timezone.utc) - timedelta(seconds=1)


def test_repeated_intake_and_source_alias_apply_one_delta(world):
    body = envelope()
    with ThreadPoolExecutor(max_workers=6) as pool:
        list(pool.map(lambda _: accept(world, body), range(12)))
    assert world.process.once() and not world.process.once()
    alias = {**body, 'messageId': str(uuid4())}
    accept(world, alias)
    assert world.process.once()
    assert world.api.ranking('run-xp')['entries'][0]['score'] == 80
    with world.db.transaction() as session:
        assert session.scalar(select(func.count()).select_from(BrokerInbox)) == 2
        assert session.scalar(select(func.count()).select_from(RunScoreReceipt)) == 1
        events = list(session.scalars(select(OutboxMessage)))
        assert len(events) == 2
        assert all(event.payload['commandHash'] for event in events)


def test_changed_message_identity_is_quarantined_without_replacing_inbox(world):
    body = envelope()
    accept(world, body)
    changed = {**body, 'payload': {**body['payload'], 'points': 999}}
    accept(world, changed); accept(world, changed)
    assert world.process.once()
    assert world.api.ranking('run-xp')['entries'][0]['score'] == 80
    with world.db.transaction() as session:
        assert session.scalar(select(func.count()).select_from(BrokerRejection)) == 1
        row = session.scalar(select(BrokerRejection))
        assert row.body_prefix == b'' and row.code == 'MessageConflict'


@pytest.mark.parametrize('raw', [b'not JSON PRIVATE', b'{"messageId":1,"messageId":2}',
    b'{"payload":NaN}', b'\xff', b'x' * 16385,
    b'{"messageId":"00000000-0000-0000-0000-000000000001","eventName":"run.rating","schemaVersion":true,"payload":{}}'])
def test_poison_is_durable_and_only_hashes_leave_in_dead_events(world, raw):
    world.accept.execute(raw); world.accept.execute(raw)
    with world.db.transaction() as session:
        assert session.scalar(select(func.count()).select_from(BrokerInbox)) == 0
        assert session.scalar(select(func.count()).select_from(BrokerRejection)) == 1
        row = session.scalar(select(BrokerRejection))
        assert row.body_size == len(raw) and row.body_prefix == b''
        event = session.scalar(select(OutboxMessage))
        assert event.destination == Destination.QUARANTINE
        assert 'PRIVATE' not in json.dumps(event.payload)


def test_business_commit_then_result_commit_failure_replays_the_saved_source(world, monkeypatch):
    from vatican_rating.persistence.postgres.repositories.broker import BrokerRepository

    body = envelope(); accept(world, body)
    original = BrokerRepository.finish_command
    monkeypatch.setattr(BrokerRepository, 'finish_command', lambda *_: (_ for _ in ()).throw(RuntimeError('Commit lost')))
    with pytest.raises(RuntimeError): world.process.once()
    assert world.api.ranking('run-xp')['entries'][0]['score'] == 80
    assert world.operations.get(UUID(body['messageId']))['status'] == 'PENDING'
    monkeypatch.setattr(BrokerRepository, 'finish_command', original)
    expire(world)
    assert world.process.once()
    assert world.api.ranking('run-xp')['entries'][0]['score'] == 80
    assert world.operations.get(UUID(body['messageId']))['status'] == 'APPLIED'


def test_business_rollback_can_be_retried_without_partial_score(world):
    body = envelope(); accept(world, body)
    with world.db.engine.begin() as connection:
        connection.execute(text("ALTER TABLE run_score_receipts ADD CONSTRAINT injected_failure CHECK (points <> 80)"))
    assert world.process.once()
    assert world.api.ranking('run-xp')['entries'] == []
    assert world.operations.get(UUID(body['messageId']))['lastError'] == 'HandlerFailed'
    with world.db.engine.begin() as connection:
        connection.execute(text('ALTER TABLE run_score_receipts DROP CONSTRAINT injected_failure'))
    expire(world); assert world.process.once()
    assert world.api.ranking('run-xp')['entries'][0]['score'] == 80


def test_rejected_board_is_replayed_with_a_new_result_generation(world):
    body = envelope(boardCode='season-2'); accept(world, body)
    assert world.process.once()
    identity = UUID(body['messageId'])
    assert world.operations.get(identity)['status'] == 'REJECTED'
    world.api.configure_board('season-2')
    assert world.operations.replay(identity)
    assert world.process.once()
    result = world.operations.get(identity)
    assert result['status'] == 'APPLIED' and result['deliveryGeneration'] == 2
    assert world.api.ranking('season-2')['entries'][0]['score'] == 80


def test_stale_processing_lease_cannot_emit_a_second_result(world):
    body = envelope(); accept(world, body)
    with world.uow() as work: old = work.broker.claim_command()
    expire(world)
    with world.uow() as work: current = work.broker.claim_command()
    reply = world.api.receive(current.message_id, 'run.rating', 1, body['payload'])
    with world.uow() as work:
        assert not work.broker.finish_command(old, InboxStatus.APPLIED, reply)
        assert work.broker.finish_command(current, InboxStatus.APPLIED, reply)
    assert len(world.operations.get(current.message_id)['events']) == 1


def test_retry_budget_and_manual_redrive_preserve_source(world):
    body = envelope(); accept(world, body)
    with world.db.transaction() as session:
        session.get(BrokerInbox, UUID(body['messageId'])).attempts = 20
    assert not world.process.once()
    assert world.operations.get(UUID(body['messageId']))['status'] == 'REVIEW'
    assert world.operations.replay(UUID(body['messageId']))
    assert world.process.once()
    assert world.api.ranking('run-xp')['entries'][0]['score'] == 80


def test_known_broker_outage_does_not_stop_durable_processing_or_burn_publication_attempts():
    from vatican_rating.worker import run_iteration
    calls = []
    class Broker:
        def receive_once(self, _): raise RuntimeError()
    class Process:
        def once(self): calls.append('process'); return True
    class Publish:
        def once(self): pytest.fail('Known disconnected broker attempted publication')
    assert run_iteration(Broker(), None, Process(), Publish()) == (True, True)
    assert run_iteration(Broker(), None, Process(), Publish(), receive=False) == (True, False)
    assert calls == ['process', 'process']


@pytest.mark.parametrize('points', [1e17, -0.0, 80.0])
def test_inbox_preserves_float_representation_and_cannot_bypass_integer_validation(world, points):
    body = envelope(points=points)
    command = Command.parse(json.dumps(body).encode())
    accept(world, body)
    with world.db.transaction() as session:
        row = session.get(BrokerInbox, command.message_id)
        assert type(row.envelope['payload']['points']) is float
        assert Command.parse(json.dumps(row.envelope).encode()).digest == command.digest
    assert world.process.once()
    assert world.api.ranking('run-xp')['entries'] == []
    assert world.operations.get(command.message_id)['status'] == 'REJECTED'
