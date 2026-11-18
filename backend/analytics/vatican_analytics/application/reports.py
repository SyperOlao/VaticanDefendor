"""Read-only reports over deduplicated warehouse facts, independent of the inbox."""
from datetime import date
from typing import Protocol

from .errors import ApplicationError


class DailyReportsGateway(Protocol):
    def daily(self, day: date) -> dict: ...


class ReadDailyReport:
    def __init__(self, gateway: DailyReportsGateway):
        self.gateway = gateway

    def execute(self, day: str) -> dict:
        try:
            parsed = date.fromisoformat(day)
            if parsed.isoformat() != day or not 1970 <= parsed.year <= 2200:
                raise ValueError()
        except (ValueError, TypeError):
            raise ApplicationError(422, 'day must be YYYY-MM-DD between 1970 and 2200') from None
        return {'day': parsed.isoformat(), 'timezone': 'UTC', **self.gateway.daily(parsed)}
