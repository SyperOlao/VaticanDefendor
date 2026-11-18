from dataclasses import dataclass
from typing import Protocol
from uuid import UUID
from ..domain.progression import ProgressionRules, XPBreakdown


@dataclass(frozen=True)
class PlayerProgress:
    account_id: UUID
    total_xp: int = 0
    current_level: int = 1
    last_ruleset_id: str | None = None


@dataclass(frozen=True)
class LevelGift:
    id: UUID
    rewarded_level: int
    ruleset_id: str
    reward_bundle_id: UUID
    status: str

    def response(self):
        return {'claimId': str(self.id), 'rewardedLevel': self.rewarded_level,
                'rulesetId': self.ruleset_id, 'rewardBundleId': str(self.reward_bundle_id), 'status': self.status}


class ProgressionRepository(Protocol):
    def compatible_release(self, ruleset_id: str, content_release_id: str) -> bool: ...
    def configure(self, rules: ProgressionRules) -> bool: ...
    def configuration(self, ruleset_id: str, published_only: bool = False) -> ProgressionRules | None: ...
    def bundles_exist(self, ids: set[UUID]) -> bool: ...
    def progress(self, account_id: UUID) -> PlayerProgress: ...
    def record_xp(self, run_id: UUID, account_id: UUID, ruleset_id: str, xp: XPBreakdown) -> None: ...
    def save_progress(self, progress: PlayerProgress) -> None: ...
    def unlock(self, account_id: UUID, ruleset_id: str, level: int, bundle_id: UUID) -> LevelGift | None: ...
    def available_gifts(self, account_id: UUID) -> list[LevelGift]: ...
