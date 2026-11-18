"""Publish an immutable leaderboard using the RUN_XP source."""
import argparse
from ..application.errors import ApplicationError
from ..application.use_cases import RatingUseCases
from ..bootstrap.di import create_container
from ..bootstrap.settings import Settings


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--code',default='run-xp')
    args = parser.parse_args()
    container = create_container(Settings.from_environment())
    try:
        container.get(RatingUseCases).configure_board(args.code)
        print('Leaderboard configured: '+args.code)
    except ApplicationError as error:
        parser.exit(1,error.detail+'\n')
    except Exception as error:
        parser.exit(1,'Leaderboard configuration failed: '+type(error).__name__+'\n')
    finally:
        container.close()


if __name__=='__main__':
    main()
