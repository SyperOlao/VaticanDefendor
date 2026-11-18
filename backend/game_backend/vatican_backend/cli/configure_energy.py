"""Publish explicit, immutable energy settings using the service's local database."""
import argparse
from ..application.energy import ConfigureEnergy
from ..application.errors import ApplicationError
from ..bootstrap.di import create_game_container
from ..bootstrap.settings import Settings
from ..domain.energy import EnergyRules


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ruleset-id", required=True)
    parser.add_argument("--max-energy", required=True, type=int)
    parser.add_argument("--regen-interval-ms", required=True, type=int)
    parser.add_argument("--energy-per-tick", required=True, type=int)
    parser.add_argument("--run-cost", required=True, type=int)
    args = parser.parse_args()
    try:
        rules = EnergyRules(args.ruleset_id, args.max_energy, args.regen_interval_ms, args.energy_per_tick, args.run_cost)
    except ValueError:
        parser.error("Energy values are outside the supported range")
    container, client = create_game_container(Settings.from_environment())
    try:
        container.get(ConfigureEnergy).execute(rules)
        print("Energy rules published. Select this version with VD_ENERGY_RULESET_ID.")
    except ApplicationError as error:
        parser.exit(1, error.detail + "\n")
    except Exception as error:
        parser.exit(1, "Energy configuration failed: " + type(error).__name__ + "\n")
    finally:
        container.close()
        client.close()


if __name__ == "__main__":
    main()
