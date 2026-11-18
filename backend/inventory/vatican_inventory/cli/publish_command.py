import argparse
from pathlib import Path

from ..bootstrap.rabbit import RabbitSettings
from ..domain.broker import MAX_BODY_BYTES
from ..integrations.rabbit import RabbitBroker


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--file", required=True, type=Path)
    args = parser.parse_args()
    broker = RabbitBroker(RabbitSettings.from_environment())
    try:
        with args.file.open("rb") as source:
            body = source.read(MAX_BODY_BYTES + 1)
        identity = broker.publish_command(body)
        print("Broker confirmed command " + str(identity))
    except Exception:
        print("Command was not confirmed; correct configuration/input or retry the same messageId")
        raise SystemExit(1) from None
    finally:
        broker.close()


if __name__ == "__main__":
    main()
