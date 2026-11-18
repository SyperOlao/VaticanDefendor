"""Publish an explicit immutable LOCAL policy after content and XP rules."""
import argparse
import json
from pathlib import Path
from ..application.local_runs import ConfigureLocalRuns
from ..application.errors import ApplicationError
from ..bootstrap.di import create_game_container
from ..bootstrap.settings import Settings
from ..domain.local_runs import LocalPolicy


def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--file',required=True,type=Path)
    args=parser.parse_args()
    try:
        if args.file.stat().st_size>100000:raise ValueError()
        policy=LocalPolicy(json.loads(args.file.read_text(encoding='utf-8')))
    except (OSError,ValueError,TypeError,KeyError):parser.error('Invalid LOCAL policy file')
    container,client=create_game_container(Settings.from_environment())
    try:
        result=container.get(ConfigureLocalRuns).execute(policy)
        print('LOCAL policy published: '+result['policyId'])
    except ApplicationError as error:parser.exit(1,error.detail+'\n')
    except Exception as error:parser.exit(1,'LOCAL publication failed: '+type(error).__name__+'\n')
    finally:container.close();client.close()


if __name__=='__main__':main()
