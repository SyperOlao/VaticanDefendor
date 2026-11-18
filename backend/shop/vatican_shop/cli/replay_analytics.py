"""Retry analytics for one completed purchase without charging or granting again."""
import argparse
from uuid import UUID
from ..application.analytics import DeliverAnalytics
from ..bootstrap.di import create_container
from ..bootstrap.settings import Settings


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('account_id',type=UUID);parser.add_argument('operation_id',type=UUID);args=parser.parse_args()
    container=create_container(Settings.from_environment())
    try:
        if not container.get(DeliverAnalytics).requeue(args.account_id,args.operation_id):
            parser.exit(1,'Purchase analytics is not in REVIEW\n')
        print('Purchase analytics requeued: '+str(args.operation_id))
    except Exception as error:parser.exit(1,'Purchase analytics replay failed: '+type(error).__name__+'\n')
    finally:container.close()


if __name__=='__main__':main()
