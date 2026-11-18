"""Retry one REVIEW analytics source without changing its payload."""
import argparse
from uuid import UUID
from ..bootstrap.di import create_container
from ..bootstrap.settings import Settings
from ..application.use_cases import ProjectEvents
from ..domain.events import EVENT, GIFT_EVENT, CASE_EVENT, PURCHASE_EVENT, WALLET_EVENT


def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('source_id',type=UUID)
    parser.add_argument('--event-name',choices=(EVENT,GIFT_EVENT,CASE_EVENT,PURCHASE_EVENT,WALLET_EVENT),default=EVENT);args=parser.parse_args()
    container=create_container(Settings.from_environment())
    try:
        if not container.get(ProjectEvents).requeue(args.source_id,args.event_name):parser.exit(1,'Source is not in REVIEW\n')
        print('Analytics source requeued: '+str(args.source_id))
    except Exception as error:parser.exit(1,'Analytics replay failed: '+type(error).__name__+'\n')
    finally:container.close()


if __name__=='__main__':main()
