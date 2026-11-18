"""Publish immutable case rules referencing existing reward bundles."""
import argparse
import json
from pathlib import Path
from ..application.cases import PublishCase
from ..application.errors import ApplicationError
from ..bootstrap.di import create_game_container
from ..bootstrap.settings import Settings
from ..domain.cases import CaseRules


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--file',type=Path,required=True)
    args = parser.parse_args()
    try:
        if args.file.stat().st_size > 1_000_000:
            raise ValueError()
        rules = CaseRules.parse(json.loads(args.file.read_text(encoding='utf-8')))
    except (OSError,ValueError,TypeError,KeyError):
        parser.error('Invalid case configuration')
    container,client = create_game_container(Settings.from_environment())
    try:
        container.get(PublishCase).execute(rules)
        print('Case published: '+str(rules.id))
    except ApplicationError as error:
        parser.exit(1,error.detail+'\n')
    except Exception as error:
        parser.exit(1,'Case publication failed: '+type(error).__name__+'\n')
    finally:
        container.close()
        client.close()


if __name__=='__main__':
    main()
