import json
from uuid import uuid4

import pytest

from vatican_inventory.bootstrap.rabbit import RabbitSettings
from vatican_inventory.domain.broker import Command, retry_seconds


def test_rabbit_environment_encodes_credentials_and_hides_them_from_repr(monkeypatch):
    monkeypatch.delenv("VD_INVENTORY_RABBITMQ_URL", raising=False)
    for name, value in {"HOST": "rabbitmq", "PORT": "5672", "USER": "service/user", "PASSWORD": "test:@/# ?",
                        "VHOST": "game/test"}.items():
        monkeypatch.setenv("VD_RABBITMQ_" + name, value)
    settings = RabbitSettings.from_environment()
    assert settings.url == "amqp://service%2Fuser:test%3A%40%2F%23%20%3F@rabbitmq:5672/game%2Ftest"
    assert "test:@/# ?" not in repr(settings) and "amqp://" not in repr(settings)
    assert retry_seconds(1) == 2 and retry_seconds(20) == 300


def test_equivalent_json_formatting_and_key_order_keep_the_same_inbox_digest():
    value = {"messageId": str(uuid4()), "eventName": "level.reward", "schemaVersion": 1, "payload": {"x": 1, "y": 2}}
    first = Command.parse(json.dumps(value, indent=2).encode())
    second = Command.parse(json.dumps(dict(reversed(list(value.items())))).encode())
    assert first.digest == second.digest


@pytest.mark.parametrize("prefix", ["../escape", "", "x" * 101])
def test_invalid_resource_prefix_cannot_create_unintended_queues(prefix):
    with pytest.raises(ValueError):
        RabbitSettings("amqp://localhost/", queue_prefix=prefix)


def test_explicit_postgres_connection_timeout_is_respected_without_changing_default(monkeypatch):
    from vatican_inventory.persistence.postgres import database
    create = database.create_engine
    captured = []
    def capture(*args, **kwargs):
        captured.append(kwargs["connect_args"]["connect_timeout"])
        return create(*args, **kwargs)
    monkeypatch.setattr(database, "create_engine", capture)
    first = database.Database("postgresql://localhost/not_connected?connect_timeout=30")
    second = database.Database("postgresql://localhost/not_connected")
    first.close()
    second.close()
    assert captured == [30, 3]


@pytest.mark.parametrize("value", ["0", "61", "bad"])
def test_invalid_postgres_connection_timeout_is_rejected(value):
    from vatican_inventory.persistence.postgres.database import Database
    with pytest.raises(ValueError):
        Database("postgresql://localhost/not_connected?connect_timeout=" + value)
