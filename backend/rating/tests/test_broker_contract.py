import json
from uuid import uuid4
import pytest

from vatican_rating.bootstrap.rabbit import RabbitSettings
from vatican_rating.domain.broker import Command, InvalidEnvelope
from vatican_backend.bootstrap.rating_rabbit import RabbitSettings as GameRabbitSettings


def test_both_services_derive_the_same_topology_and_encode_credentials(monkeypatch):
    monkeypatch.delenv('VD_RATING_RABBITMQ_URL', raising=False)
    for key, value in {'USER': 'rating/user', 'PASSWORD': 'private:@/#', 'HOST': 'rabbitmq',
                       'PORT': '5672', 'VHOST': 'test/vhost'}.items():
        monkeypatch.setenv('VD_RABBITMQ_' + key, value)
    rating, game = RabbitSettings.from_environment(), GameRabbitSettings.from_environment()
    assert rating.url == game.url == 'amqp://rating%2Fuser:private%3A%40%2F%23@rabbitmq:5672/test%2Fvhost'
    assert rating.commands_exchange == game.commands_exchange
    assert rating.results_queue == game.results_queue
    assert 'private' not in repr(rating) + repr(game)


def test_command_hash_is_stable_under_json_formatting_and_metadata_id_is_checked():
    body = {'messageId': str(uuid4()), 'eventName': 'run.rating', 'schemaVersion': 1, 'payload': {'points': 0}}
    first = Command.parse(json.dumps(body).encode())
    assert first.digest == Command.parse(json.dumps(body, indent=2, sort_keys=True).encode()).digest
    with pytest.raises(InvalidEnvelope): Command.parse(json.dumps(body).encode(), str(uuid4()))
