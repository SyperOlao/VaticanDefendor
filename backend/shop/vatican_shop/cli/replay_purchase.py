import argparse
import getpass
from uuid import UUID, uuid4
from ..bootstrap.di import create_container
from ..bootstrap.settings import Settings
from ..application.errors import ApplicationError
from ..application.operations import AdminPurchases


def main():
    parser = argparse.ArgumentParser(description="Resume a purchase in Review after reconciling participants")
    parser.add_argument("account_id", type=UUID)
    parser.add_argument("operation_id", type=UUID)
    parser.add_argument("--reason", required=True)
    parser.add_argument("--expected-version", type=int, required=True)
    parser.add_argument("--request-key", type=UUID, default=None)
    args = parser.parse_args()
    container = create_container(Settings.from_environment())
    try:
        token = getpass.getpass("Auth administrator access token: ")
        admin = container.get(AdminPurchases)
        request_key = args.request_key or uuid4()
        print("Retry request key:", request_key)
        result = admin.retry(token, args.account_id, args.operation_id, args.expected_version, request_key, args.reason)
        print("Purchase queued for idempotent reconciliation; job version:", result["jobVersion"])
    except ApplicationError as error:
        parser.exit(1, error.detail + "\n")
    finally:
        container.close()


if __name__ == "__main__":
    main()
