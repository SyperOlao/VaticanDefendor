"""Publish explicit immutable permanent upgrade prices and cumulative bonuses."""
import argparse,json
from pathlib import Path
from ..application.prestige import PublishPrestige
from ..application.errors import ApplicationError
from ..domain.prestige import PrestigeRules
from ..bootstrap.di import create_game_container
from ..bootstrap.settings import Settings


def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--file',required=True,type=Path);args=parser.parse_args()
    try:
        if args.file.stat().st_size>100000:raise ValueError()
        rules=PrestigeRules(json.loads(args.file.read_text(encoding='utf-8')))
    except (OSError,ValueError,TypeError,KeyError):parser.error('Invalid prestige configuration')
    container,client=create_game_container(Settings.from_environment())
    try:
        reply=container.get(PublishPrestige).execute(rules);print('Prestige configuration published: '+reply['rulesetId'])
    except ApplicationError as error:parser.exit(1,error.detail+'\n')
    except Exception as error:parser.exit(1,'Prestige publication failed: '+type(error).__name__+'\n')
    finally:container.close();client.close()


if __name__=='__main__':main()
