"""One-time, administrator-authorized import of a legacy JSON catalog."""
import argparse
import getpass
import json
from pathlib import Path

from ..application.content import AdminCatalog
from ..application.errors import ApplicationError
from ..bootstrap.di import create_container
from ..bootstrap.settings import Settings
from ..domain.catalogs import validate_catalog


def read_catalog(path):
    try:
        return validate_catalog(json.loads(Path(path).read_text(encoding="utf-8")))
    except (OSError, ValueError, TypeError):
        raise ApplicationError(422, "Invalid catalog import file") from None


def main():
    parser = argparse.ArgumentParser(description="Import missing offers as an auth administrator; existing offers must match")
    parser.add_argument("path", type=Path)
    args = parser.parse_args()
    container = create_container(Settings.from_environment())
    try:
        token = getpass.getpass("Auth administrator access token: ")
        result = container.get(AdminCatalog).import_catalog(token, read_catalog(args.path))
        print(f"Imported: {result['created']}; unchanged: {result['unchanged']}")
    except ApplicationError as error:
        parser.exit(1, error.detail + "\n")
    finally:
        container.close()


if __name__ == "__main__":
    main()
