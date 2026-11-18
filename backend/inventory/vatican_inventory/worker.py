import argparse
import logging
import signal
from threading import Event

from .application.broker import AcceptBrokerDelivery, ProcessBrokerCommands, PublishBrokerEvents
from .application.health import HealthUseCases
from .bootstrap.broker_di import create_broker_container
from .bootstrap.rabbit import RabbitSettings
from .bootstrap.settings import Settings
from .integrations.rabbit import RabbitBroker


def run_iteration(broker, receiver, process, publish):
    # Broker outages must not stop processing commands already accepted into PostgreSQL.
    receive_failed = False
    try:
        received = broker.receive_once(receiver)
    except Exception:
        received, receive_failed = False, True
    processed = process.once()
    published = publish.once()
    return received or processed or published, receive_failed


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--once", action="store_true")
    args = parser.parse_args()
    logging.getLogger("pika").setLevel(logging.CRITICAL)
    stopped = Event()
    for name in (signal.SIGTERM, signal.SIGINT):
        signal.signal(name, lambda *_: stopped.set())
    container = create_broker_container(Settings.from_environment(), RabbitSettings.from_environment())
    try:
        broker = container.get(RabbitBroker)
        receive, process, publish = (container.get(cls) for cls in
            (AcceptBrokerDelivery, ProcessBrokerCommands, PublishBrokerEvents))
        health = container.get(HealthUseCases)
        receive_failures = 0
        while not stopped.is_set():
            try:
                health.ready()
                worked, receive_failed = run_iteration(broker, receive, process, publish)
            except Exception as error:
                logging.getLogger(__name__).error("Inventory worker iteration failed exception=%s", type(error).__name__)
                broker.close()
                if args.once:
                    raise SystemExit(1) from None
                stopped.wait(1)
                continue
            if args.once:
                if receive_failed:
                    raise SystemExit(1)
                return
            if receive_failed:
                receive_failures += 1
                if receive_failures == 1 or receive_failures & (receive_failures - 1) == 0:
                    logging.getLogger(__name__).warning("Inventory broker receive unavailable; durable commands remain queued")
                stopped.wait(1)
            else:
                receive_failures = 0
                if not worked:
                    try:
                        broker.idle(0.25)
                    except Exception:
                        broker.close()
                        stopped.wait(1)
    finally:
        container.close()


if __name__ == "__main__":
    main()
