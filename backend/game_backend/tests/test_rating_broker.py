import json
from uuid import uuid4, uuid5

import pytest
from sqlalchemy import func, select

from vatican_backend.application.rating_broker import AcceptRatingResult
from vatican_backend.bootstrap.di import create_game_container
from vatican_backend.bootstrap.settings import Settings
from vatican_backend.application.messaging import DeliverRewards
from vatican_backend.domain.rating_broker import command_hash
from vatican_backend.persistence.postgres.tables import OutboxMessage, RatingResultMessage
from vatican_backend.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory


@pytest.fixture
def source(service_storage_factory):
    db, url = service_storage_factory('game_backend')
    message = {'id': uuid4(), 'destination': 'rating', 'event_name': 'run.rating', 'schema_version': 1,
               'payload': {'accountId': str(uuid4()), 'runId': str(uuid4()), 'boardCode': 'run-xp',
                           'rulesetId': 'xp-v1', 'contentReleaseId': 'release-v1', 'points': 80}}
    with db.transaction() as session:
        session.add(OutboxMessage(aggregate_id=uuid4(), **message))
    return db, message, AcceptRatingResult(PostgresUnitOfWorkFactory(db))


def result(message, status='APPLIED', generation=1):
    payload = {'messageId': str(message['id']), 'status': status, 'deliveryGeneration': generation,
               'commandHash': command_hash(message)}
    if status == 'APPLIED':
        payload['receipt'] = {**message['payload'], 'boardId': str(uuid4()), 'scoreAfter': 80}
    else:
        payload['code'] = 'HandlerUnavailable'
    return {'messageId': str(uuid5(message['id'], f'rating.broker:rabbit.results:{generation}')),
            'eventName': 'rating.command.result', 'schemaVersion': 1, 'payload': payload}


def accept(receiver, event):
    receiver.execute(json.dumps(event).encode(), event['messageId'], event['payload']['messageId'],
                     'application/json', 'utf-8')


@pytest.mark.parametrize('change', ['command', 'receipt', 'event', 'version', 'generation', 'source', 'properties'])
def test_invalid_or_mismatched_result_is_durably_quarantined_before_ack(source, change):
    db, message, receiver = source
    event = result(message)
    if change == 'command': event['payload']['commandHash'] = '0' * 64
    elif change == 'receipt': event['payload']['receipt']['accountId'] = str(uuid4())
    elif change == 'event': event['eventName'] = 'other.result'
    elif change == 'version': event['schemaVersion'] = True
    elif change == 'generation': event['payload']['deliveryGeneration'] = 0
    elif change == 'source': event = result({**message, 'id': uuid4()})
    if change == 'properties':
        for _ in range(2): receiver.execute(json.dumps(event).encode(), str(uuid4()), str(message['id']))
    else:
        accept(receiver, event); accept(receiver, event)
    with db.transaction() as session:
        assert session.get(OutboxMessage, message['id']).published_at is None
        rows = list(session.scalars(select(RatingResultMessage)))
        assert rows and all(row.outcome == 'QUARANTINED' for row in rows)


def test_duplicate_result_and_conflicting_payload_do_not_replace_completed_receipt(source):
    db, message, receiver = source
    event = result(message)
    accept(receiver, event); accept(receiver, event)
    changed = json.loads(json.dumps(event))
    changed['payload']['receipt']['scoreAfter'] = 999
    accept(receiver, changed)
    with db.transaction() as session:
        assert session.get(OutboxMessage, message['id']).receipt_json == event['payload']['receipt']
        assert session.scalar(select(func.count()).select_from(RatingResultMessage)
            .where(RatingResultMessage.outcome == 'APPLIED')) == 1
        assert session.scalar(select(func.count()).select_from(RatingResultMessage)
            .where(RatingResultMessage.outcome == 'QUARANTINED')) == 1


def test_late_rejection_never_reverses_an_applied_source(source):
    db, message, receiver = source
    applied = result(message, generation=2)
    accept(receiver, applied)
    accept(receiver, result(message, 'REVIEW', 1))
    accept(receiver, result(message, 'REJECTED', 3))
    with db.transaction() as session:
        row = session.get(OutboxMessage, message['id'])
        assert row.published_at is not None and row.failed_at is None
        assert row.receipt_json == applied['payload']['receipt']


def test_verified_success_is_conclusive_even_if_older_than_a_review_result(source):
    db, message, receiver = source
    accept(receiver, result(message, 'REVIEW', 2))
    accept(receiver, result(message, 'APPLIED', 1))
    with db.transaction() as session:
        row = session.get(OutboxMessage, message['id'])
        assert row.published_at is not None and row.failed_at is None and row.broker_result_generation == 2


def test_stale_rejection_does_not_overwrite_a_newer_review(source):
    db, message, receiver = source
    accept(receiver, result(message, 'REVIEW', 2))
    accept(receiver, result(message, 'REJECTED', 1))
    with db.transaction() as session:
        assert session.get(OutboxMessage, message['id']).last_error == 'RatingReview'


def test_confirmation_racing_with_publisher_does_not_restore_pending(source):
    db, message, receiver = source
    uow = PostgresUnitOfWorkFactory(db)
    with uow() as work: job = work.messages.claim()
    accept(receiver, result(message))
    with uow() as work:
        assert not work.messages.dispatched(job['id'], job['lease'])
        work.messages.retry(job['id'], job['lease'])
    with db.transaction() as session:
        assert session.get(OutboxMessage, message['id']).published_at is not None


def test_rabbit_gateway_is_selected_by_di_and_owned_connection_is_closed(monkeypatch):
    import vatican_backend.integrations.rating_rabbit as transport
    closed = []
    class Broker:
        def __init__(self, settings): pass
        def close(self): closed.append(True)
    monkeypatch.setenv('VD_RATING_RABBITMQ_URL', 'amqp://localhost/test')
    monkeypatch.setattr(transport, 'RatingRabbitBroker', Broker)
    container, client = create_game_container(Settings('postgresql://unused', {}, 120, rating_transport='rabbitmq'))
    try:
        assert isinstance(container.get(DeliverRewards).gateway, transport.RatingRabbitDelivery)
    finally:
        container.close(); client.close()
    assert closed == [True]
