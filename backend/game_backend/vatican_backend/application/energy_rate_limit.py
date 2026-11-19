from threading import Lock
from time import monotonic_ns
from uuid import UUID


_SECOND_NS = 1_000_000_000
_REFILL_NS = 5 * _SECOND_NS
_CAPACITY = 4
_FULL_REFILL_NS = _CAPACITY * _REFILL_NS


class EnergyReadLimiter:
    """Token bucket for energy reads in the game API process."""

    def __init__(self):
        self._lock = Lock()
        self._full_at: dict[UUID, int] = {}
        self._prune_at = 0

    def retry_after(self, account_id: UUID) -> int:
        """Consume one request allowance or return the wait in whole seconds."""
        with self._lock:
            now = monotonic_ns()
            if now >= self._prune_at:
                self._full_at = {account: deadline for account, deadline in self._full_at.items() if deadline > now}
                self._prune_at = now + _FULL_REFILL_NS

            full_at = self._full_at.get(account_id, now)
            # The bucket can accumulate at most four requests' worth of refill debt.
            wait = full_at + _REFILL_NS - _FULL_REFILL_NS - now
            if wait > 0:
                return (wait + _SECOND_NS - 1) // _SECOND_NS

            self._full_at[account_id] = max(full_at, now) + _REFILL_NS
            return 0
