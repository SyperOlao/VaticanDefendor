import argparse
import time
import logging
from .bootstrap.di import create_container
from .bootstrap.settings import Settings
from .application.use_cases import SettlePurchases
from .application.analytics import DeliverAnalytics
from .application.mock_payments import VerifyMockPayments, FulfillMockPayments


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--once',action='store_true');args=parser.parse_args()
    settings=Settings.from_environment()
    container=create_container(settings)
    try:
        purchases=container.get(SettlePurchases);analytics=container.get(DeliverAnalytics)
        queues=[('purchase',purchases),('analytics',analytics)]
        if getattr(settings,'mock_payments_enabled',False):
            queues.extend([('mock.verify',container.get(VerifyMockPayments)),('mock.fulfill',container.get(FulfillMockPayments))])
        while True:
            worked=False;failed=False
            # Both queues get an opportunity even if the other one fails.
            for label,service in queues:
                try:worked=service.once() or worked
                except Exception as error:
                    logging.getLogger(__name__).error('Shop %s iteration failed exception=%s',label,type(error).__name__)
                    failed=True
            if args.once:
                if failed:raise SystemExit(1) from None
                return
            if failed or not worked:time.sleep(1)
    finally:container.close()


if __name__=='__main__':main()
