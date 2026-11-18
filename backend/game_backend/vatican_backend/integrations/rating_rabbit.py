"""Thread-owned AMQP connection; database work is committed before acknowledging."""
import json
import pika

from ..application.errors import ApplicationError
from ..application.ports import PendingDelivery
from ..domain.rating_broker import command_envelope


class RatingBrokerUnavailable(Exception):
    pass


class RatingRabbitBroker:
    def __init__(self, settings):
        self.settings = settings
        self.connection = self.channel = None

    @staticmethod
    def declare_topology(channel, settings):
        arguments = {'x-queue-type': 'quorum', 'x-delivery-limit': -1,
                     'x-max-length-bytes': 67_108_864, 'x-overflow': 'reject-publish'}
        for exchange, queue in ((settings.commands_exchange, settings.commands_queue),
                                (settings.results_exchange, settings.results_queue),
                                (settings.dead_exchange, settings.dead_queue)):
            channel.exchange_declare(exchange=exchange, exchange_type='direct', durable=True)
            channel.queue_declare(queue=queue, durable=True, arguments=arguments)
            channel.queue_bind(queue=queue, exchange=exchange, routing_key='rating')

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
            parameters.heartbeat, parameters.blocked_connection_timeout = 60, 5
            parameters.socket_timeout, parameters.stack_timeout = 5, 10
            parameters.connection_attempts = 1
            self.connection = pika.BlockingConnection(parameters)
            self.channel = self.connection.channel()
            self.declare_topology(self.channel, self.settings)
            self.channel.basic_qos(prefetch_count=1)
            self.channel.confirm_delivery()
        except Exception:
            self.close()
            raise RatingBrokerUnavailable() from None

    def publish_command(self, message):
        body = json.dumps(command_envelope(message), sort_keys=True, separators=(',', ':'), allow_nan=False).encode()
        try:
            self.connect()
            self.channel.basic_publish(exchange=self.settings.commands_exchange, routing_key='rating',
                body=body, mandatory=True, properties=pika.BasicProperties(content_type='application/json',
                    content_encoding='utf-8', delivery_mode=2, message_id=str(message['id']),
                    correlation_id=str(message['id']), type=message['event_name'], app_id='vatican_backend'))
        except Exception:
            self.close()
            raise RatingBrokerUnavailable() from None

    def receive_once(self, receiver):
        try:
            self.connect()
            method, properties, body = self.channel.basic_get(self.settings.results_queue, auto_ack=False)
            if method is None:
                return False
            receiver.execute(body, properties.message_id, properties.correlation_id,
                             properties.content_type, properties.content_encoding)
            self.channel.basic_ack(method.delivery_tag)
            return True
        except Exception:
            self.close()
            raise RatingBrokerUnavailable() from None

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


class RatingRabbitDelivery:
    def __init__(self, broker, http):
        self.broker, self.http = broker, http

    def deliver(self, message):
        if message['destination'] != 'rating':
            return self.http.deliver(message)
        if message['event_name'] != 'run.rating' or message['schema_version'] != 1:
            raise ApplicationError(422, 'Unsupported rating command')
        try:
            self.broker.publish_command(message)
        except RatingBrokerUnavailable:
            raise ApplicationError(503, 'Rating broker is unavailable') from None
        return PendingDelivery()
