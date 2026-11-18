import argparse
import logging
import signal
from threading import Event

from .application.rating_broker import AcceptRatingResult
from .application.profiles import HealthUseCases
from .bootstrap.di import create_game_container
from .bootstrap.settings import Settings
from .bootstrap.rating_rabbit import RabbitSettings
from .integrations.rating_rabbit import RatingRabbitBroker


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--once', action='store_true')
    args = parser.parse_args()
    logging.getLogger('pika').setLevel(logging.CRITICAL)
    stopped = Event()
    for name in (signal.SIGTERM, signal.SIGINT):
        signal.signal(name, lambda *_: stopped.set())
    container, client = create_game_container(Settings.from_environment())
    broker = RatingRabbitBroker(RabbitSettings.from_environment())
    try:
        receiver = container.get(AcceptRatingResult)
        health = container.get(HealthUseCases)
        failures = 0
        while not stopped.is_set():
            try:
                health.ready()
                received = broker.receive_once(receiver)
                failures = 0
                if args.once:
                    return
                if not received:
                    broker.idle(.25)
            except Exception as error:
                broker.close()
                failures += 1
                if failures == 1 or failures & (failures - 1) == 0:
                    logging.getLogger(__name__).warning('Rating result delivery unavailable exception=%s', type(error).__name__)
                if args.once:
                    raise SystemExit(1) from None
                stopped.wait(min(30, 2 ** min(failures, 5)))
    finally:
        broker.close()
        container.close()
        client.close()


if __name__ == '__main__':
    main()
