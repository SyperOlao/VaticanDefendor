from datetime import datetime, timedelta, timezone
import os
from pathlib import Path
import time
from types import SimpleNamespace
from uuid import uuid4

import pytest
from sqlalchemy import select

from vatican_rating.application.broker import AcceptBrokerDelivery, ProcessBrokerCommands, PublishBrokerEvents
from vatican_rating.application.use_cases import RatingUseCases
from vatican_rating.bootstrap.rabbit import RabbitSettings
from vatican_rating.integrations.rabbit import BrokerUnavailable, RabbitBroker
from vatican_rating.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as RatingUow
from vatican_rating.persistence.postgres.tables import BrokerInbox, OutboxMessage as RatingOutbox
from vatican_backend.application.messaging import DeliverRewards
from vatican_backend.application.rating_broker import AcceptRatingResult
from vatican_backend.integrations.rating_rabbit import RatingRabbitBroker, RatingRabbitDelivery
from vatican_backend.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory as GameUow
from vatican_backend.persistence.postgres.tables import OutboxMessage as GameOutbox


@pytest.fixture(autouse=True)
def worker_module_paths(monkeypatch):
    # conftest modifies sys.path in the pytest process, but crash/CLI tests start
    # fresh interpreters. They must also work when pytest is launched from repo root.
    backend = Path(__file__).resolve().parents[2]
    roots = [str(backend / name) for name in ('auth', 'game_backend', 'wallet', 'inventory', 'shop', 'rating', 'analytics')]
    if os.environ.get('PYTHONPATH'):
        roots.append(os.environ['PYTHONPATH'])
    monkeypatch.setenv('PYTHONPATH', os.pathsep.join(roots))


@pytest.fixture
def rabbit_settings():
    url = os.environ.get('VD_TEST_RABBITMQ_URL')
    if not url:
        pytest.skip('Set VD_TEST_RABBITMQ_URL to an isolated RabbitMQ 4.1+ vhost')
    prefix = 'rating_test_' + uuid4().hex
    settings = RabbitSettings(url, prefix + '.rating', prefix)
    admin = RabbitBroker(settings)
    deadline = time.monotonic() + 30
    while True:
        try:
            admin.connect()
            break
        except BrokerUnavailable:
            if time.monotonic() >= deadline:
                pytest.fail('Disposable RabbitMQ did not complete an AMQP handshake')
            time.sleep(.2)
    try:
        yield settings
    finally:
        admin.close()
        admin.connect()
        for queue in (settings.commands_queue, settings.results_queue, settings.dead_queue):
            admin.channel.queue_delete(queue=queue)
        for exchange in (settings.commands_exchange, settings.results_exchange, settings.dead_exchange):
            admin.channel.exchange_delete(exchange=exchange)
        admin.close()


@pytest.fixture
def amqp_world(service_storage_factory, rabbit_settings):
    game_db, game_url = service_storage_factory('game_backend')
    rating_db, rating_url = service_storage_factory('rating')
    source, run_id, account = uuid4(), uuid4(), uuid4()
    payload = {'accountId': str(account), 'runId': str(run_id), 'boardCode': 'run-xp',
               'rulesetId': 'xp-v1', 'contentReleaseId': 'release-v1', 'points': 80}
    with game_db.transaction() as session:
        session.add(GameOutbox(id=source, aggregate_id=run_id, destination='rating',
                              event_name='run.rating', schema_version=1, payload=payload))
    sender, receiver = RatingRabbitBroker(rabbit_settings), RabbitBroker(rabbit_settings)
    game_uow, rating_uow = GameUow(game_db), RatingUow(rating_db)
    rating = RatingUseCases(rating_uow)

    class NoHTTP:
        def deliver(self, message):
            pytest.fail('Rating RabbitMQ delivery attempted HTTP fallback')

    w = SimpleNamespace(game=game_db, game_url=game_url, rating_db=rating_db, rating_url=rating_url,
        source=source, payload=payload, sender=sender, receiver=receiver, settings=rabbit_settings,
        game_uow=game_uow, rating_uow=rating_uow, rating=rating,
        deliver=DeliverRewards(game_uow, RatingRabbitDelivery(sender, NoHTTP())),
        accept=AcceptBrokerDelivery(rating_uow), process=ProcessBrokerCommands(rating_uow, rating),
        publish=PublishBrokerEvents(rating_uow, receiver), result=AcceptRatingResult(game_uow))
    try:
        yield w
    finally:
        sender.close(); receiver.close()


def ready_again(w):
    for db, model in ((w.game, GameOutbox), (w.rating_db, RatingOutbox), (w.rating_db, BrokerInbox)):
        with db.transaction() as session:
            for row in session.scalars(select(model)):
                row.available_at = datetime.now(timezone.utc) - timedelta(seconds=1)


def complete(w):
    assert w.deliver.once()
    assert w.receiver.receive_once(w.accept)
    assert w.process.once()
    assert w.publish.once()
    assert w.sender.receive_once(w.result)
