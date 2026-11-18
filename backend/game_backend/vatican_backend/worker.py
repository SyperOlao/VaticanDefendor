import argparse
import logging
import time
from .bootstrap.di import create_game_container
from .bootstrap.settings import Settings
from .application.messaging import DeliverRewards
from .application.runs import RunUseCases
from .application.local_runs import LocalRunUseCases


def main():
    logging.getLogger('pika').setLevel(logging.CRITICAL)
    parser = argparse.ArgumentParser()
    parser.add_argument("--once", action="store_true")
    args = parser.parse_args()
    container, client = create_game_container(Settings.from_environment())
    try:
        use_case = container.get(DeliverRewards)
        runs = container.get(RunUseCases)
        local_runs = container.get(LocalRunUseCases)
        maintenance_at = 0
        while True:
            cleanup_failed = False
            if time.monotonic() >= maintenance_at:
                for name, maintain in (("assignments", runs.expire_pending), ("local runs", local_runs.maintain)):
                    try:
                        maintain()
                    except Exception as error:
                        logging.getLogger(__name__).error("Maintenance failed task=%s exception=%s", name, type(error).__name__)
                        cleanup_failed = True
                maintenance_at = time.monotonic() + 1
            try:
                delivered = use_case.once()
            except Exception as error:
                logging.getLogger(__name__).error("Worker iteration failed exception=%s", type(error).__name__)
                if args.once:
                    raise SystemExit(1) from None
                time.sleep(1)
                continue
            if args.once:
                if cleanup_failed:
                    raise SystemExit(1)
                return
            if not delivered:
                time.sleep(1)
    finally:
        container.close()
        client.close()


if __name__ == "__main__":
    main()
