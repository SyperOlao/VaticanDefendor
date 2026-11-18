import argparse
from uuid import UUID

from ..application.broker import BrokerOperations
from ..bootstrap.di import create_container
from ..bootstrap.settings import Settings


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("identity", type=UUID)
    parser.add_argument("--outbox", action="store_true", help="Retry a failed result/dead-letter event by eventId")
    args = parser.parse_args()
    container = create_container(Settings.from_environment())
    try:
        replayed = container.get(BrokerOperations).replay(args.identity, outbox=args.outbox)
        print("Requeued" if replayed else "No failed operation found")
    finally:
        container.close()


if __name__ == "__main__":
    main()
