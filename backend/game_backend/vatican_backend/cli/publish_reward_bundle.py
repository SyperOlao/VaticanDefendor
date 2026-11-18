"""Publish one immutable, versioned reward bundle."""
import argparse
import json
from pathlib import Path
from ..application.errors import ApplicationError
from ..application.level_rewards import PublishRewardBundle
from ..bootstrap.di import create_game_container
from ..bootstrap.settings import Settings
from ..domain.level_rewards import RewardPlan


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--file',required=True,type=Path)
    args=parser.parse_args()
    try:
        if args.file.stat().st_size>1_000_000:
            raise ValueError()
        value=json.loads(args.file.read_text(encoding='utf-8'))
        if set(value)!={'schemaVersion','bundleId','code','version','items'}:
            raise ValueError()
        plan=RewardPlan.parse({k:v for k,v in value.items() if k not in ('code','version')})
    except (OSError,ValueError,TypeError,KeyError):
        parser.error('Invalid or unsupported reward bundle')
    container,client=create_game_container(Settings.from_environment())
    try:
        container.get(PublishRewardBundle).execute(value['code'],value['version'],plan)
        print('Reward bundle published: '+str(plan.bundle_id))
    except ApplicationError as error:
        parser.exit(1,error.detail+'\n')
    except Exception as error:
        parser.exit(1,'Reward publication failed: '+type(error).__name__+'\n')
    finally:
        container.close()
        client.close()


if __name__=='__main__':
    main()
