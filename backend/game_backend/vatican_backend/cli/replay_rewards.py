import argparse
from uuid import UUID
from ..bootstrap.di import create_game_container
from ..bootstrap.settings import Settings
from ..application.messaging import DeliverRewards


def main():
    parser = argparse.ArgumentParser(description="Requeue one failed reward after fixing its cause")
    parser.add_argument("message_id", type=UUID)
    args = parser.parse_args()
    container, client = create_game_container(Settings.from_environment())
    try:
        if not container.get(DeliverRewards).requeue(args.message_id):
            parser.exit(1, "No failed, unpublished reward with that ID\n")
        print("Reward queued for another idempotent delivery")
    finally:
        container.close()
        client.close()


if __name__ == "__main__":
    main()
