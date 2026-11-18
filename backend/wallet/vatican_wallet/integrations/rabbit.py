"""One thread owns this Pika connection; business work never runs in a broker callback."""
import json

import pika

from ..domain.broker import Command, Destination


class BrokerUnavailable(Exception):
    pass


class RabbitBroker:
    def __init__(self, settings):
        self.settings = settings
        self.connection = self.channel = None

    def connect(self):
        if self.connection is not None and self.connection.is_open and self.channel is not None and self.channel.is_open:
            try:
                self.connection.process_data_events(time_limit=0)
                if self.connection.is_open and self.channel.is_open:
                    return
            except Exception:
                pass
        self.close()
        try:
            parameters = pika.URLParameters(self.settings.url)
            parameters.heartbeat = 60
            parameters.blocked_connection_timeout = 5
            parameters.socket_timeout = 5
            parameters.stack_timeout = 10
            parameters.connection_attempts = 1
            self.connection = pika.BlockingConnection(parameters)
            self.channel = self.connection.channel()
            self.declare_topology(self.channel, self.settings)
            self.channel.basic_qos(prefetch_count=1)
            self.channel.confirm_delivery()
        except Exception:
            self.close()
            raise BrokerUnavailable() from None

    @staticmethod
    def declare_topology(channel, settings):
        arguments = {"x-queue-type": "quorum", "x-delivery-limit": -1,
                     "x-max-length-bytes": 67_108_864, "x-overflow": "reject-publish"}
        for exchange, queue in ((settings.commands_exchange, settings.commands_queue),
                                (settings.dead_exchange, settings.dead_queue)):
            channel.exchange_declare(exchange=exchange, exchange_type="direct", durable=True)
            channel.queue_declare(queue=queue, durable=True, arguments=arguments)
            channel.queue_bind(queue=queue, exchange=exchange, routing_key="wallet")

    def receive_once(self, receiver):
        self.connect()
        try:
            method, properties, body = self.channel.basic_get(self.settings.commands_queue, auto_ack=False)
            if method is None:
                return False
            # Any persistence failure leaves the delivery unacknowledged. Closing requeues it.
            receiver.execute(body, properties.message_id, properties.content_type, properties.content_encoding)
            self.channel.basic_ack(method.delivery_tag)
            return True
        except Exception:
            self.close()
            raise BrokerUnavailable() from None

    def publish(self, event):
        self.connect()
        exchange = {Destination.DEAD: self.settings.dead_exchange,
                    Destination.QUARANTINE: self.settings.dead_exchange}[event.destination]
        body = json.dumps({"messageId": str(event.id), "eventName": event.event_name,
                           "schemaVersion": event.schema_version, "payload": event.payload},
                          sort_keys=True, separators=(",", ":"), allow_nan=False).encode()
        try:
            self.channel.basic_publish(exchange=exchange, routing_key="wallet", body=body, mandatory=True,
                properties=pika.BasicProperties(content_type="application/json", content_encoding="utf-8",
                    delivery_mode=2, message_id=str(event.id), correlation_id=str(event.aggregate_id),
                    type=event.event_name, app_id="vatican_wallet"))
            # confirm_delivery makes Nack or Basic.Return an exception, never a successful publish.
        except Exception:
            self.close()
            raise BrokerUnavailable() from None

    def publish_command(self, body):
        command = Command.parse(body)
        self.connect()
        try:
            self.channel.basic_publish(exchange=self.settings.commands_exchange, routing_key="wallet",
                body=json.dumps(command.envelope, sort_keys=True, separators=(",", ":")).encode(), mandatory=True,
                properties=pika.BasicProperties(content_type="application/json", content_encoding="utf-8", delivery_mode=2,
                    message_id=str(command.message_id), correlation_id=str(command.message_id),
                    type=command.envelope["eventName"], app_id="wallet_command_producer"))
        except Exception:
            self.close()
            raise BrokerUnavailable() from None
        return command.message_id

    def idle(self, seconds):
        if self.connection is not None and self.connection.is_open:
            self.connection.sleep(seconds)

    def close(self):
        connection, self.connection, self.channel = self.connection, None, None
        if connection is not None and connection.is_open:
            try:
                connection.close()
            except Exception:
                pass
