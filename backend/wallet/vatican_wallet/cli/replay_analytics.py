"""Retry delivery of one REVIEW ledger event without changing player money."""
import argparse
from uuid import UUID
from ..application.analytics import DeliverAnalytics
from ..bootstrap.di import create_container
from ..bootstrap.settings import Settings


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("transaction_id", type=UUID)
    args = parser.parse_args()
    container = create_container(Settings.from_environment())
    try:
        if not container.get(DeliverAnalytics).requeue(args.transaction_id):
            parser.exit(1, "Wallet analytics is not in REVIEW\n")
        print("Wallet analytics requeued: " + str(args.transaction_id))
    except Exception as error:
        parser.exit(1, "Wallet analytics replay failed: " + type(error).__name__ + "\n")
    finally:
        container.close()


if __name__ == "__main__":
    main()
