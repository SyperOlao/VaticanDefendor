"""Grant one idempotent regeneration effect from a trusted operator workflow."""
import argparse
from uuid import UUID
from ..application.energy import EnergyUseCases
from ..application.errors import ApplicationError
from ..bootstrap.di import create_game_container
from ..bootstrap.settings import Settings


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("account_id", type=UUID)
    parser.add_argument("--source-key", required=True)
    parser.add_argument("--rate-bonus", required=True)
    parser.add_argument("--duration-seconds", required=True, type=int)
    args = parser.parse_args()
    container, client = create_game_container(Settings.from_environment())
    try:
        effect = container.get(EnergyUseCases).grant_regen_effect(
            args.account_id, args.source_key, args.rate_bonus, args.duration_seconds)
        print("Effect applied: " + str(effect.id))
    except ApplicationError as error:
        parser.exit(1, error.detail + "\n")
    except Exception as error:
        parser.exit(1, "Effect grant failed: " + type(error).__name__ + "\n")
    finally:
        container.close()
        client.close()


if __name__ == "__main__":
    main()
