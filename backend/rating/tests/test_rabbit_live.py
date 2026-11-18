import json
import os
import subprocess
import sys
import time

import pika
import pytest
from sqlalchemy import func, select, text

from vatican_rating.application.broker import BrokerOperations
from vatican_rating.integrations.rabbit import BrokerUnavailable
from vatican_rating.persistence.postgres.tables import BrokerInbox, OutboxMessage as RatingOutbox
from vatican_backend.integrations.rating_rabbit import RatingBrokerUnavailable
from vatican_backend.persistence.postgres.tables import OutboxMessage as GameOutbox, RatingResultMessage
from .conftest import ready_again


def points(w):
    entries = w.rating.ranking('run-xp')['entries']
    return entries[0]['score'] if entries else 0


def source(w):
    with w.game.transaction() as session:
        row = session.get(GameOutbox, w.source)
        return row.published_at, row.failed_at, row.receipt_json, row.broker_published_at


def test_live_command_confirm_is_pending_until_validated_result_and_all_io_is_outside_db(amqp_world, monkeypatch):
    w = amqp_world
    original = w.sender.publish_command
    def send(message):
        assert w.game.engine.pool.checkedout() == w.rating_db.engine.pool.checkedout() == 0
        original(message)
    monkeypatch.setattr(w.sender, 'publish_command', send)
    assert w.deliver.once()
    assert source(w)[:3] == (None, None, None) and source(w)[3] is not None
    assert points(w) == 0
    assert w.receiver.receive_once(w.accept) and w.process.once()
    original_publish = w.receiver.publish
    def publish(job):
        assert w.game.engine.pool.checkedout() == w.rating_db.engine.pool.checkedout() == 0
        original_publish(job)
    monkeypatch.setattr(w.receiver, 'publish', publish)
    assert w.publish.once() and w.sender.receive_once(w.result)
    assert source(w)[0] is not None and source(w)[2]['scoreAfter'] == 80 and points(w) == 80


def test_live_lost_producer_confirm_republishes_the_same_command_without_double_points(amqp_world, monkeypatch):
    w = amqp_world
    w.sender.connect()
    original = w.sender.channel.basic_publish
    def lost(*args, **kwargs):
        original(*args, **kwargs)
        raise OSError('Injected lost confirmation')
    monkeypatch.setattr(w.sender.channel, 'basic_publish', lost)
    assert w.deliver.once()
    assert source(w)[0] is None and source(w)[3] is None
    assert w.receiver.receive_once(w.accept) and w.process.once()
    ready_again(w)
    assert w.deliver.once()
    assert w.receiver.receive_once(w.accept) and not w.process.once()
    assert w.publish.once() and w.sender.receive_once(w.result)
    assert points(w) == 80 and source(w)[0] is not None
    with w.rating_db.transaction() as session:
        assert session.scalar(select(func.count()).select_from(BrokerInbox)) == 1


def test_live_input_database_commit_failure_keeps_delivery_unacknowledged(amqp_world):
    w = amqp_world; w.deliver.once()
    with w.rating_db.engine.begin() as connection:
        connection.execute(text('ALTER TABLE broker_inbox ADD CONSTRAINT injected_intake CHECK (false)'))
    with pytest.raises(BrokerUnavailable): w.receiver.receive_once(w.accept)
    with w.rating_db.engine.begin() as connection:
        assert connection.scalar(text('SELECT count(*) FROM broker_inbox')) == 0
        connection.execute(text('ALTER TABLE broker_inbox DROP CONSTRAINT injected_intake'))
    assert w.receiver.receive_once(w.accept) and w.process.once() and w.publish.once()
    assert w.sender.receive_once(w.result) and points(w) == 80


def test_real_worker_process_death_after_inbox_commit_before_ack_requeues_once(amqp_world):
    w = amqp_world; w.deliver.once()
    code = '''import os
from vatican_rating.bootstrap.rabbit import RabbitSettings
from vatican_rating.integrations.rabbit import RabbitBroker
from vatican_rating.persistence.postgres.database import Database
from vatican_rating.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory
from vatican_rating.application.broker import AcceptBrokerDelivery
b=RabbitBroker(RabbitSettings(os.environ['TEST_AMQP'],os.environ['TEST_QUEUE'],os.environ['TEST_EXCHANGE']))
b.connect()
m,p,body=b.channel.basic_get(b.settings.commands_queue,auto_ack=False)
assert m is not None
AcceptBrokerDelivery(PostgresUnitOfWorkFactory(Database(os.environ['TEST_DB']))).execute(body,p.message_id,p.content_type,p.content_encoding)
os._exit(9)
'''
    env = {**os.environ, 'TEST_AMQP': w.settings.url, 'TEST_QUEUE': w.settings.queue_prefix,
           'TEST_EXCHANGE': w.settings.exchange_prefix, 'TEST_DB': w.rating_url}
    child = subprocess.run([sys.executable, '-c', code], env=env, capture_output=True, timeout=30)
    assert child.returncode == 9
    for _ in range(50):
        if w.receiver.receive_once(w.accept): break
        time.sleep(.05)
    else: pytest.fail('Unacknowledged delivery did not return after process death')
    assert w.process.once() and not w.process.once()
    assert w.publish.once() and w.sender.receive_once(w.result) and points(w) == 80


def test_live_lost_result_publication_confirm_and_lost_game_ack_are_idempotent(amqp_world, monkeypatch):
    w = amqp_world
    w.deliver.once(); w.receiver.receive_once(w.accept); w.process.once()
    original = w.receiver.channel.basic_publish
    def lost(*args, **kwargs):
        original(*args, **kwargs)
        raise OSError('Injected confirm loss')
    monkeypatch.setattr(w.receiver.channel, 'basic_publish', lost)
    assert w.publish.once()
    with w.rating_db.transaction() as session:
        assert session.scalar(select(RatingOutbox.published_at)) is None
    w.sender.connect()
    method, properties, body = w.sender.channel.basic_get(w.settings.results_queue, auto_ack=False)
    assert method is not None
    event_id = json.loads(body)['messageId']
    w.result.execute(body, properties.message_id, properties.correlation_id, properties.content_type, properties.content_encoding)
    w.sender.close()  # Result committed, ACK lost.
    assert w.sender.receive_once(w.result)
    ready_again(w); assert w.publish.once() and w.sender.receive_once(w.result)
    assert source(w)[0] is not None and points(w) == 80
    with w.game.transaction() as session:
        assert session.scalar(select(func.count()).select_from(RatingResultMessage)) == 1
        assert str(session.scalar(select(RatingResultMessage.event_id))) == event_id


def test_live_result_database_rollback_requeues_without_partial_game_confirmation(amqp_world):
    w = amqp_world
    w.deliver.once(); w.receiver.receive_once(w.accept); w.process.once(); w.publish.once()
    with w.game.engine.begin() as connection:
        connection.execute(text('ALTER TABLE rating_result_messages ADD CONSTRAINT injected_result CHECK (false)'))
    with pytest.raises(RatingBrokerUnavailable): w.sender.receive_once(w.result)
    assert source(w)[0] is None and points(w) == 80
    with w.game.engine.begin() as connection:
        connection.execute(text('ALTER TABLE rating_result_messages DROP CONSTRAINT injected_result'))
    assert w.sender.receive_once(w.result) and source(w)[0] is not None and points(w) == 80


def test_live_unroutable_result_is_retried_with_confirm_after_binding_recovers(amqp_world):
    w = amqp_world
    w.deliver.once(); w.receiver.receive_once(w.accept); w.process.once()
    w.receiver.channel.queue_unbind(queue=w.settings.results_queue,
        exchange=w.settings.results_exchange, routing_key='rating')
    assert w.publish.once()
    with w.rating_db.transaction() as session:
        row = session.scalar(select(RatingOutbox))
        assert row.published_at is None and row.last_error == 'PublicationUnavailable'
    ready_again(w); assert w.publish.once() and w.sender.receive_once(w.result)
    assert points(w) == 80 and source(w)[0] is not None


def test_rejected_board_redrive_completes_reviewed_game_source(amqp_world):
    w = amqp_world
    with w.game.transaction() as session:
        row = session.get(GameOutbox, w.source)
        row.payload = {**row.payload, 'boardCode': 'season-next'}
    w.deliver.once(); w.receiver.receive_once(w.accept); w.process.once()
    for _ in range(3):
        if not w.publish.once(): break
    assert w.sender.receive_once(w.result)
    assert source(w)[0] is None and source(w)[1] is not None
    w.rating.configure_board('season-next')
    assert BrokerOperations(w.rating_uow).replay(w.source)
    assert w.process.once()
    # A dead-letter from generation 1 can precede the new result in the local outbox.
    for _ in range(5):
        if not w.publish.once(): break
    assert w.sender.receive_once(w.result)
    assert source(w)[0] is not None and source(w)[1] is None
    assert w.rating.ranking('season-next')['entries'][0]['score'] == 80


def test_late_result_completes_even_after_game_receipt_wait_budget_exhausted(amqp_world):
    w = amqp_world
    with w.game.transaction() as session: session.get(GameOutbox, w.source).attempts = 19
    assert w.deliver.once() and source(w)[1] is not None
    assert w.receiver.receive_once(w.accept) and w.process.once() and w.publish.once()
    assert w.sender.receive_once(w.result)
    assert source(w)[0] is not None and source(w)[1] is None and points(w) == 80


def test_live_malformed_commands_go_to_durable_dead_queue_without_raw_content(amqp_world):
    w = amqp_world; w.sender.connect()
    w.sender.channel.basic_publish(exchange=w.settings.commands_exchange, routing_key='rating',
        body=b'bad JSON PRIVATE_TOKEN', mandatory=True,
        properties=pika.BasicProperties(delivery_mode=2, content_type='application/json'))
    assert w.receiver.receive_once(w.accept) and w.publish.once()
    method, props, body = w.receiver.channel.basic_get(w.settings.dead_queue, auto_ack=False)
    assert method is not None and b'PRIVATE_TOKEN' not in body
    assert json.loads(body)['payload']['code'] == 'InvalidEnvelope'
    w.receiver.channel.basic_ack(method.delivery_tag)
    assert points(w) == 0


def test_real_broker_crash_recovers_confirmed_commands_and_results(amqp_world):
    name = os.environ.get('VD_TEST_RABBITMQ_CONTAINER', '')
    if not name.startswith('vatican-rating-amqp-rabbit-'):
        pytest.skip('Crash test requires the explicitly owned disposable broker')
    w = amqp_world

    def restart():
        subprocess.run(['docker', 'kill', '--signal', 'KILL', name], check=True, capture_output=True)
        with pytest.raises((RatingBrokerUnavailable, BrokerUnavailable)):
            w.sender.receive_once(w.result)
        subprocess.run(['docker', 'start', name], check=True, capture_output=True)
        for _ in range(90):
            try:
                w.sender.close(); w.receiver.close()
                w.sender.connect(); w.receiver.connect()
                return
            except (RatingBrokerUnavailable, BrokerUnavailable):
                pass
            time.sleep(.2)
        pytest.fail('Disposable RabbitMQ did not recover')

    assert w.deliver.once() and source(w)[0] is None
    restart()
    assert w.receiver.receive_once(w.accept) and w.process.once() and w.publish.once()
    restart()
    assert w.sender.receive_once(w.result) and points(w) == 80 and source(w)[0] is not None


def test_real_process_death_after_score_commit_recovers_using_original_receipt(amqp_world):
    w = amqp_world
    w.deliver.once(); w.receiver.receive_once(w.accept)
    code = '''import os
from vatican_rating.persistence.postgres.database import Database
from vatican_rating.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory
from vatican_rating.application.use_cases import RatingUseCases
uow=PostgresUnitOfWorkFactory(Database(os.environ['TEST_DB']))
with uow() as work: job=work.broker.claim_command()
assert job is not None
RatingUseCases(uow).receive(job.message_id,job.envelope['eventName'],job.envelope['schemaVersion'],job.envelope['payload'])
os._exit(9)
'''
    child = subprocess.run([sys.executable, '-c', code], env={**os.environ, 'TEST_DB': w.rating_url},
                           capture_output=True, timeout=30)
    assert child.returncode == 9 and points(w) == 80
    ready_again(w)
    assert w.process.once() and w.publish.once() and w.sender.receive_once(w.result)
    assert points(w) == 80 and source(w)[0] is not None


def test_worker_entrypoints_deliver_and_confirm_over_real_amqp(amqp_world):
    w = amqp_world
    assert w.deliver.once()
    env = {**os.environ, 'VD_SERVICE_SECRET': 's' * 40, 'VD_RATING_DATABASE_URL': w.rating_url,
           'VD_DATABASE_URL': w.game_url, 'VD_RATING_RABBITMQ_URL': w.settings.url,
           'VD_RATING_RABBIT_QUEUE_PREFIX': w.settings.queue_prefix,
           'VD_RATING_RABBIT_EXCHANGE_PREFIX': w.settings.exchange_prefix,
           'VD_RATING_TRANSPORT': 'rabbitmq', 'VD_SERVER_POOL_JSON': '[]', 'VD_RUN_REWARDS_JSON': '{}'}
    for module in ('vatican_rating.worker', 'vatican_backend.rating_worker'):
        child = subprocess.run([sys.executable, '-m', module, '--once'], env=env,
                               capture_output=True, timeout=30)
        assert child.returncode == 0, 'Worker failed: ' + module
    assert points(w) == 80 and source(w)[0] is not None
