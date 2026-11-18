import argparse
import logging
import signal
import time
from threading import Event

from .application.broker import AcceptBrokerDelivery, ProcessBrokerCommands, PublishBrokerEvents
from .application.health import HealthUseCases
from .bootstrap.broker_di import create_broker_container
from .bootstrap.rabbit import RabbitSettings
from .bootstrap.settings import Settings
from .integrations.rabbit import RabbitBroker


def run_iteration(broker, receiver, process, publish, *, receive=True):
    receive_failed = False
    received = False
    if receive:
        try:
            received = broker.receive_once(receiver)
        except Exception:
            receive_failed = True
    processed = process.once()
    published = publish.once() if receive and not receive_failed else False
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
        receive_at = 0
        while not stopped.is_set():
            try:
                health.ready()
                network_due = time.monotonic() >= receive_at
                worked, receive_failed = run_iteration(broker, receive, process, publish, receive=network_due)
            except Exception as error:
                logging.getLogger(__name__).error("Rating worker iteration failed exception=%s", type(error).__name__)
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
                    logging.getLogger(__name__).warning("Rating broker receive unavailable; durable commands remain queued")
                receive_at = time.monotonic() + min(30, 2 ** min(receive_failures, 5))
            elif network_due:
                receive_failures = 0
            if not worked:
                if network_due and not receive_failed:
                    try:
                        broker.idle(0.25)
                    except Exception:
                        broker.close()
                        stopped.wait(1)
                else:
                    stopped.wait(.25)
    finally:
        container.close()


if __name__ == "__main__":
    main()
