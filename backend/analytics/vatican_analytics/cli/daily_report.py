"""Print a deduplicated UTC daily report as JSON, without opening the inbox DB."""
import argparse
import json
import os

from ..application.errors import ApplicationError
from ..application.reports import ReadDailyReport
from ..persistence.clickhouse.database import Database
from ..persistence.clickhouse.repositories.reports import ClickHouseDailyReports


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('day', help='UTC day in YYYY-MM-DD format')
    args = parser.parse_args()
    url = os.getenv('VD_ANALYTICS_DATABASE_URL')
    if not url:
        parser.exit(1, 'Set VD_ANALYTICS_DATABASE_URL to the ClickHouse HTTP URL\n')
    database = None
    try:
        database = Database(url)
        report = ReadDailyReport(ClickHouseDailyReports(database)).execute(args.day)
        print(json.dumps(report, indent=2))
    except ApplicationError as error:
        parser.exit(1, error.detail + '\n')
    except Exception as error:
        parser.exit(1, 'Daily report failed: ' + type(error).__name__ + '\n')
    finally:
        if database is not None:
            database.close()


if __name__ == '__main__':
    main()
