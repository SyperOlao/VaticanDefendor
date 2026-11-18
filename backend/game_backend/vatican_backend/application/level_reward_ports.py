from __future__ import annotations
from dataclasses import dataclass
from datetime import datetime
from typing import Protocol
from uuid import UUID
from ..domain.level_rewards import RewardLine, RewardPlan


@dataclass(frozen=True)
class ClaimState:
    id: UUID
    account_id: UUID
    rewarded_level: int
    ruleset_id: str
    bundle_id: UUID
    status: str
    requested_at: datetime | None
    claimed_at: datetime | None
    snapshot: dict | None


class LevelRewardsRepository(Protocol):
    def publish_bundle(self, code: str, version: int, plan: RewardPlan) -> bool: ...
    def claim(self, account_id: UUID, claim_id: UUID) -> ClaimState | None: ...
    def list(self, account_id: UUID, limit: int, offset: int) -> list[dict]: ...
    def bundle_snapshot(self, bundle_id: UUID) -> dict | None: ...
    def validate_references(self, lines: tuple[RewardLine, ...]) -> bool: ...
    def grants(self, claim_id: UUID) -> list[dict]: ...
    def begin(self, claim_id: UUID, snapshot: dict, at: datetime) -> None: ...
    def add_grant(self, claim_id: UUID, bundle_id: UUID, line: RewardLine, message_id: UUID | None,
                  receipt: dict | None = None) -> None: ...
    def finalize(self, claim_id: UUID, at: datetime) -> None: ...
