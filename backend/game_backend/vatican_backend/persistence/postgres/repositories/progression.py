from uuid import uuid5
from sqlalchemy import func, select
from sqlalchemy.dialects.postgresql import insert
from ....application.catalog import LevelClaimStatus, RulesetStatus
from ....application.progression_ports import LevelGift, PlayerProgress
from ....domain.progression import ProgressionRules, XPRule, LevelDefinition
from .level_rewards import LevelRewardsRepository
from ..tables import (ProgressionConfiguration, PlayerLevelDefinition, PlayerLevelProgress,
    PlayerLevelRewardClaim, PlayerXPTransaction, RewardBundle, Ruleset, RunXPBreakdown, XPRewardRule)


class ProgressionRepository:
    def __init__(self, session):
        self.session = session

    def configuration(self, ruleset_id, published_only=False):
        query = select(ProgressionConfiguration).join(Ruleset, Ruleset.id == ProgressionConfiguration.ruleset_id).where(
            ProgressionConfiguration.ruleset_id == ruleset_id)
        if published_only:
            query = query.where(Ruleset.status == RulesetStatus.PUBLISHED)
        row = self.session.scalar(query)
        if row is None:
            return None
        rules = ProgressionRules.from_snapshot(row.snapshot)
        if rules.ruleset_id != ruleset_id or rules.digest() != row.payload_hash:
            raise ValueError('Stored progression configuration is corrupt')
        return rules

    def compatible_release(self, ruleset_id, content_release_id):
        row = self.session.get(Ruleset, ruleset_id)
        return row is not None and row.content_release_id in (None, content_release_id)

    def bundles_exist(self, ids):
        if not ids:
            return True
        return set(self.session.scalars(select(RewardBundle.id).where(RewardBundle.id.in_(ids)))) == ids

    def configure(self, rules):
        self.session.execute(select(func.pg_advisory_xact_lock(func.hashtextextended('progression:' + rules.ruleset_id, 0))))
        existing = self.session.get(ProgressionConfiguration, rules.ruleset_id)
        if existing:
            return self.configuration(rules.ruleset_id) == rules
        self.session.execute(insert(Ruleset).values(id=rules.ruleset_id, code=rules.ruleset_id,
            version=1, status=RulesetStatus.PUBLISHED).on_conflict_do_nothing(index_elements=[Ruleset.id]))
        if self.session.get(Ruleset, rules.ruleset_id).status != RulesetStatus.PUBLISHED:
            return False
        old_rules = list(self.session.scalars(select(XPRewardRule).where(XPRewardRule.ruleset_id == rules.ruleset_id)))
        old_levels = list(self.session.scalars(select(PlayerLevelDefinition).where(PlayerLevelDefinition.ruleset_id == rules.ruleset_id)
                                              .order_by(PlayerLevelDefinition.level)))
        # Adopt an exact legacy configuration; never replace definitions referenced by existing claims.
        if old_rules and {(r.source_type, r.target_key, r.xp_per_unit, r.max_xp_per_run) for r in old_rules} != {
                (r.source_type, r.target_key, r.xp_per_unit, r.max_xp_per_run) for r in rules.rules}:
            return False
        if old_levels and tuple(LevelDefinition(r.level, r.total_xp_required, r.reward_bundle_id) for r in old_levels) != rules.levels:
            return False
        if not old_rules:
            self.session.add_all(XPRewardRule(ruleset_id=rules.ruleset_id, source_type=r.source_type,
                target_key=r.target_key, xp_per_unit=r.xp_per_unit, max_xp_per_run=r.max_xp_per_run) for r in rules.rules)
        if not old_levels:
            self.session.add_all(PlayerLevelDefinition(ruleset_id=rules.ruleset_id, level=d.level,
                total_xp_required=d.total_xp_required, reward_bundle_id=d.reward_bundle_id) for d in rules.levels)
        self.session.add(ProgressionConfiguration(ruleset_id=rules.ruleset_id, payload_hash=rules.digest(), snapshot=rules.snapshot()))
        self.session.flush()
        return True

    def progress(self, account_id):
        row = self.session.get(PlayerLevelProgress, account_id)
        return PlayerProgress(row.account_id, row.total_xp, row.current_level, row.last_ruleset_id) if row else PlayerProgress(account_id)

    def record_xp(self, run_id, account_id, ruleset_id, xp):
        self.session.add(RunXPBreakdown(run_id=run_id, ruleset_id=ruleset_id,
            hero_kill_xp=xp.hero_kill_xp, tower_kill_xp=xp.tower_kill_xp,
            tycoon_xp=xp.tycoon_xp, victory_xp=xp.victory_xp, total_xp=xp.total_xp))
        self.session.flush()  # XP transaction references the persisted breakdown, in this same transaction.
        self.session.add(PlayerXPTransaction(id=uuid5(run_id, 'player.xp'), account_id=account_id,
                                             run_id=run_id, xp_delta=xp.total_xp))

    def save_progress(self, progress):
        statement = insert(PlayerLevelProgress).values(account_id=progress.account_id, total_xp=progress.total_xp,
            current_level=progress.current_level, last_ruleset_id=progress.last_ruleset_id)
        self.session.execute(statement.on_conflict_do_update(index_elements=[PlayerLevelProgress.account_id],
            set_={name: getattr(statement.excluded, name) for name in ('total_xp', 'current_level', 'last_ruleset_id')}))

    def unlock(self, account_id, ruleset_id, level, bundle_id):
        claim_id = uuid5(account_id, 'level.reward:' + str(level))
        created = self.session.scalar(insert(PlayerLevelRewardClaim).values(id=claim_id, account_id=account_id,
            rewarded_level=level, ruleset_id=ruleset_id, reward_bundle_id=bundle_id, status=LevelClaimStatus.AVAILABLE,
            reward_snapshot=LevelRewardsRepository(self.session).bundle_snapshot(bundle_id))
            .on_conflict_do_nothing(index_elements=[PlayerLevelRewardClaim.account_id, PlayerLevelRewardClaim.rewarded_level])
            .returning(PlayerLevelRewardClaim.id))
        return LevelGift(created, level, ruleset_id, bundle_id, LevelClaimStatus.AVAILABLE) if created else None

    def available_gifts(self, account_id):
        rows = self.session.scalars(select(PlayerLevelRewardClaim).where(PlayerLevelRewardClaim.account_id == account_id,
            PlayerLevelRewardClaim.status == LevelClaimStatus.AVAILABLE).order_by(PlayerLevelRewardClaim.rewarded_level))
        return [LevelGift(r.id, r.rewarded_level, r.ruleset_id, r.reward_bundle_id, r.status) for r in rows]
