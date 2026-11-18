from datetime import datetime, timedelta, timezone
import json
import os
from uuid import UUID, uuid4

import pika
import pytest
from sqlalchemy import func, select

from vatican_inventory.application.broker import AcceptBrokerDelivery, ProcessBrokerCommands, PublishBrokerEvents
from vatican_inventory.bootstrap.rabbit import RabbitSettings
from vatican_inventory.integrations.rabbit import BrokerUnavailable, RabbitBroker
from vatican_inventory.persistence.postgres.tables import BrokerInbox, OutboxMessage


@pytest.fixture
def rabbit():
    url = os.getenv("VD_TEST_RABBITMQ_URL")
    if not url:
        pytest.skip("Set VD_TEST_RABBITMQ_URL to an isolated RabbitMQ 4.1+ vhost")
    namespace = "inventory_test_" + uuid4().hex
    settings = RabbitSettings(url, namespace + ".inventory", namespace)
    broker = RabbitBroker(settings)
    broker.connect()
    try:
        yield broker
    finally:
        broker.connect()
        for queue in (settings.commands_queue, settings.results_queue, settings.dead_queue):
            broker.channel.queue_delete(queue=queue)
        for exchange in (settings.commands_exchange, settings.results_exchange, settings.dead_exchange):
            broker.channel.exchange_delete(exchange=exchange)
        broker.close()


def send(broker, account, *, body=None):
    value = {"messageId": str(uuid4()), "eventName": "level.reward", "schemaVersion": 1,
             "payload": {"accountId": str(account), "claimId": str(uuid4()), "itemIndex": 0,
                         "rewardType": "ITEM", "rewardRef": "kit", "quantity": 3}}
    broker.channel.basic_publish(exchange=broker.settings.commands_exchange, routing_key="inventory",
        body=body if body is not None else json.dumps(value).encode(), mandatory=True,
        properties=pika.BasicProperties(delivery_mode=2, content_type="application/json",
                                        message_id=value["messageId"] if body is None else None))
    return value


def test_live_receipt_ack_result_confirm_and_persistent_event(inventory, rabbit):
    value = send(rabbit, inventory.account)
    assert rabbit.receive_once(AcceptBrokerDelivery(inventory.api.uow))
    assert not rabbit.receive_once(AcceptBrokerDelivery(inventory.api.uow))
    assert ProcessBrokerCommands(inventory.api.uow, inventory.api).once()
    assert PublishBrokerEvents(inventory.api.uow, rabbit).once()
    method, properties, body = rabbit.channel.basic_get(rabbit.settings.results_queue, auto_ack=False)
    assert method is not None and properties.delivery_mode == 2
    event = json.loads(body)
    assert properties.message_id == event["messageId"]
    assert properties.correlation_id == value["messageId"]
    assert event["payload"]["receipt"]["quantityAfter"] == 3
    rabbit.channel.basic_ack(method.delivery_tag)
    with inventory.database.transaction() as session:
        assert session.scalar(select(OutboxMessage.published_at)) is not None


def test_live_db_failure_before_ack_requeues_the_original_delivery(inventory, rabbit):
    value = send(rabbit, inventory.account)
    class UnavailableDatabase:
        def execute(self, *args):
            raise RuntimeError("Injected DB outage before accept commit")
    with pytest.raises(BrokerUnavailable):
        rabbit.receive_once(UnavailableDatabase())
    rabbit.connect()
    method, properties, body = rabbit.channel.basic_get(rabbit.settings.commands_queue, auto_ack=False)
    assert method is not None and method.redelivered
    assert json.loads(body)["messageId"] == value["messageId"]
    rabbit.close()  # Redelivery remains unacknowledged and returns to the same durable queue.
    assert rabbit.receive_once(AcceptBrokerDelivery(inventory.api.uow))
    assert ProcessBrokerCommands(inventory.api.uow, inventory.api).once()
    assert inventory.api.item(inventory.account, "item:kit")["quantity"] == 3


def test_live_crash_between_inbox_commit_and_input_ack_is_idempotent(inventory, rabbit):
    value = send(rabbit, inventory.account)
    method, properties, body = rabbit.channel.basic_get(rabbit.settings.commands_queue, auto_ack=False)
    AcceptBrokerDelivery(inventory.api.uow).execute(body, properties.message_id, properties.content_type)
    rabbit.close()  # No ACK was sent, although inbox committed.
    assert rabbit.receive_once(AcceptBrokerDelivery(inventory.api.uow))
    process = ProcessBrokerCommands(inventory.api.uow, inventory.api)
    assert process.once() and not process.once()
    with inventory.database.transaction() as session:
        assert session.scalar(select(func.count()).select_from(BrokerInbox)) == 1
        assert session.get(BrokerInbox, UUID(value["messageId"])).status == "APPLIED"
    assert inventory.api.item(inventory.account, "item:kit")["quantity"] == 3


def test_live_malformed_message_reaches_dead_queue_with_no_raw_payload(inventory, rabbit):
    send(rabbit, inventory.account, body=b"bad JSON containing PRIVATE password")
    assert rabbit.receive_once(AcceptBrokerDelivery(inventory.api.uow))
    assert PublishBrokerEvents(inventory.api.uow, rabbit).once()
    method, properties, body = rabbit.channel.basic_get(rabbit.settings.dead_queue, auto_ack=False)
    assert method is not None and properties.delivery_mode == 2
    assert json.loads(body)["payload"]["code"] == "InvalidEnvelope"
    assert b"PRIVATE" not in body and b"password" not in body
    rabbit.channel.basic_ack(method.delivery_tag)


def test_live_unroutable_result_is_not_marked_published_and_retries_after_reconnect(inventory, rabbit):
    send(rabbit, inventory.account)
    rabbit.receive_once(AcceptBrokerDelivery(inventory.api.uow))
    ProcessBrokerCommands(inventory.api.uow, inventory.api).once()
    rabbit.channel.queue_unbind(queue=rabbit.settings.results_queue,
        exchange=rabbit.settings.results_exchange, routing_key="inventory")
    publisher = PublishBrokerEvents(inventory.api.uow, rabbit)
    assert publisher.once()
    with inventory.database.transaction() as session:
        row = session.scalar(select(OutboxMessage))
        assert row.published_at is None and row.last_error == "PublicationUnavailable"
        identity = row.id
        row.available_at = datetime.now(timezone.utc) - timedelta(seconds=1)
    assert publisher.once()  # Reconnect declares the binding, then requires publisher confirm.
    method, properties, body = rabbit.channel.basic_get(rabbit.settings.results_queue, auto_ack=False)
    assert method is not None and json.loads(body)["messageId"] == str(identity)
    rabbit.channel.basic_ack(method.delivery_tag)
