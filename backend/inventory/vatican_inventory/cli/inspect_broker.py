import argparse
import json
from uuid import UUID

from ..application.broker import BrokerOperations
from ..application.errors import ApplicationError
from ..bootstrap.di import create_container
from ..bootstrap.settings import Settings
from ..domain.broker import InspectionKind


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("kind", choices=list(InspectionKind))
    parser.add_argument("--id", type=UUID)
    parser.add_argument("--status")
    parser.add_argument("--code")
    parser.add_argument("--cursor")
    parser.add_argument("--limit", type=int, default=50)
    args = parser.parse_args()
    if ((args.kind == InspectionKind.REJECTIONS and args.status)
            or (args.kind != InspectionKind.REJECTIONS and args.code)):
        parser.error("Use --code for rejections and --status for commands/events")
    container = None
    try:
        container = create_container(Settings.from_environment())
        operations = container.get(BrokerOperations)
        if args.id:
            result = operations.get(args.id) if args.kind == InspectionKind.COMMANDS else operations.detail(args.kind, args.id)
        else:
            result = operations.list(args.kind, args.limit, args.cursor, args.code if args.kind == InspectionKind.REJECTIONS else args.status)
        print(json.dumps(result, ensure_ascii=False, indent=2))
    except ApplicationError as error:
        print(error.detail)
        raise SystemExit(1) from None
    except Exception as error:
        print("Broker inspection failed: " + type(error).__name__)
        raise SystemExit(1) from None
    finally:
        if container is not None:
            container.close()


if __name__ == "__main__":
    main()
