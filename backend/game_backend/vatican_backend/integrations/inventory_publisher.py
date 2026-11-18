"""Isolated publication with a hard deadline, including publisher-confirm waits."""
import json
import os
import subprocess
import sys
from threading import Timer

import pika

from ..application.errors import ApplicationError

PUBLISH_TIMEOUT_SECONDS = 12


class RabbitInventoryPublisher:
    def __init__(self, settings):
        self.settings = settings

    def publish(self, command):
        # BlockingConnection has no public deadline for every channel RPC/confirm.
        # A disposable process bounds the entire operation and closes the socket
        # on timeout without accessing Pika internals or sharing a connection.
        request = {"settings": {"url": self.settings.url, "queue_prefix": self.settings.queue_prefix,
                                "exchange_prefix": self.settings.exchange_prefix}, "command": command}
        try:
            result = subprocess.run([sys.executable, "-m", "vatican_backend.integrations.inventory_publisher"],
                input=json.dumps(request, allow_nan=False).encode(), stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL, timeout=PUBLISH_TIMEOUT_SECONDS, check=False,
                env={**os.environ, "PYTHONPATH": os.pathsep.join(path for path in sys.path if isinstance(path, str))})
            if result.returncode != 0:
                raise RuntimeError("Publication was not confirmed")
        except Exception:
            raise ApplicationError(503, "Inventory command publication is unavailable") from None

    def _publish_in_process(self, command):
        connection = None
        try:
            parameters = pika.URLParameters(self.settings.url)
            parameters.heartbeat = 30
            parameters.blocked_connection_timeout = 3
            parameters.socket_timeout = 3
            parameters.stack_timeout = 6
            parameters.connection_attempts = 1
            connection = pika.BlockingConnection(parameters)
            channel = connection.channel()
            channel.exchange_declare(exchange=self.settings.commands_exchange, exchange_type="direct", durable=True)
            channel.queue_declare(queue=self.settings.commands_queue, durable=True, arguments={
                "x-queue-type": "quorum", "x-delivery-limit": -1,
                "x-max-length-bytes": 67_108_864, "x-overflow": "reject-publish"})
            channel.queue_bind(queue=self.settings.commands_queue,
                               exchange=self.settings.commands_exchange, routing_key="inventory")
            channel.confirm_delivery()
            confirmed = channel.basic_publish(exchange=self.settings.commands_exchange, routing_key="inventory",
                body=json.dumps(command, sort_keys=True, separators=(",", ":"), allow_nan=False).encode(), mandatory=True,
                properties=pika.BasicProperties(content_type="application/json", content_encoding="utf-8", delivery_mode=2,
                    message_id=command["messageId"], correlation_id=command["messageId"],
                    type=command["eventName"], app_id="vatican_backend"))
            if confirmed is False:
                raise RuntimeError("Publication was not confirmed")
        except Exception:
            raise ApplicationError(503, "Inventory command publication is unavailable") from None
        finally:
            if connection is not None and connection.is_open:
                try:
                    connection.close()
                except Exception:
                    pass  # A confirmed publish stays confirmed even if connection shutdown fails.


def main():
    from ..bootstrap.inventory_rabbit import RabbitSettings
    # This also bounds an orphaned publisher if the purchase worker itself dies.
    watchdog = Timer(PUBLISH_TIMEOUT_SECONDS, lambda: os._exit(2))
    watchdog.daemon = True
    watchdog.start()
    try:
        request = json.load(sys.stdin)
        RabbitInventoryPublisher(RabbitSettings(**request["settings"]))._publish_in_process(request["command"])
    except Exception:
        raise SystemExit(1) from None
    finally:
        watchdog.cancel()


if __name__ == "__main__":
    main()
