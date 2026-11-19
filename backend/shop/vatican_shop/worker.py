import argparse
import logging
import os
from pathlib import Path
import time

from sqlalchemy.exc import OperationalError

from .application.analytics import DeliverAnalytics
from .application.mock_payments import VerifyMockPayments, FulfillMockPayments
from .application.use_cases import SettlePurchases
from .bootstrap.di import create_container
from .bootstrap.settings import Settings
from .persistence.postgres.database import Database


logger = logging.getLogger(__name__)


def run_iteration(queues, database):
    worked = failed = False
    # Both queues get an opportunity even if the other one fails.
    for label, service in queues:
        try:
            worked = service.once() or worked
        except OperationalError as error:
            failed = True
            state = getattr(error.orig, "sqlstate", None)
            reconnect = (error.connection_invalidated or not state or
                         state.startswith("08") or state in {"57P01", "57P02", "57P03"})
            if reconnect:
                # Retire pooled connections only after the failed transaction
                # unwinds. The next iteration uses the existing durable job.
                database.close()
            logger.error("Shop %s database error sqlstate=%s pool_reset=%s", label, state or "-", reconnect)
        except Exception as error:
            failed = True
            logger.error("Shop %s iteration failed exception=%s", label, type(error).__name__)
    return worked, failed


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--once", action="store_true")
    args = parser.parse_args()
    health_path = os.getenv("VD_SHOP_WORKER_HEALTH_FILE")
    heartbeat = Path(health_path) if health_path and not args.once else None
    if heartbeat is not None:
        heartbeat.unlink(missing_ok=True)

    settings = Settings.from_environment()
    container = create_container(settings)
    try:
        database = container.get(Database)
        queues = [("purchase", container.get(SettlePurchases)),
                  ("analytics", container.get(DeliverAnalytics))]
        if settings.mock_payments_enabled:
            queues.extend([("mock.verify", container.get(VerifyMockPayments)),
                           ("mock.fulfill", container.get(FulfillMockPayments))])
        while True:
            worked, failed = run_iteration(queues, database)
            if args.once:
                if failed:
                    raise SystemExit(1)
                return
            if not failed and heartbeat is not None:
                heartbeat.touch()
            if failed or not worked:
                time.sleep(1)
    finally:
        container.close()


if __name__ == "__main__":
    main()
