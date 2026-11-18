import argparse
import logging
import time
from .bootstrap.di import create_container
from .bootstrap.settings import Settings
from .application.use_cases import ProjectEvents


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--once',action='store_true');args=parser.parse_args()
    container=create_container(Settings.from_environment())
    try:
        worker=container.get(ProjectEvents)
        while True:
            try:worked=worker.once()
            except Exception as error:
                logging.getLogger(__name__).error('Analytics projection failed exception=%s',type(error).__name__)
                if args.once:raise SystemExit(1) from None
                time.sleep(1);continue
            if args.once:return
            if not worked:time.sleep(1)
    finally:container.close()


if __name__=='__main__':main()
