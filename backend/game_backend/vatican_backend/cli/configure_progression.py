"""Publish an immutable XP policy and level thresholds from an explicit JSON file."""
import argparse
import json
from pathlib import Path
from ..application.errors import ApplicationError
from ..application.progression import ConfigureProgression
from ..bootstrap.di import create_game_container
from ..bootstrap.settings import Settings
from ..domain.progression import ProgressionRules


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--file', required=True, type=Path)
    args = parser.parse_args()
    try:
        if args.file.stat().st_size > 5_000_000:
            raise ValueError()
        value = json.loads(args.file.read_text(encoding='utf-8'))
        rules = ProgressionRules.from_snapshot(value)
    except (OSError, ValueError, TypeError, AttributeError):
        parser.error('Invalid progression configuration; check rule caps and contiguous level thresholds')
    container, client = create_game_container(Settings.from_environment())
    try:
        container.get(ConfigureProgression).execute(rules)
        print('Progression rules published for ' + rules.ruleset_id)
    except ApplicationError as error:
        parser.exit(1, error.detail + '\n')
    except Exception as error:
        parser.exit(1, 'Progression publication failed: ' + type(error).__name__ + '\n')
    finally:
        container.close()
        client.close()


if __name__ == '__main__':
    main()
