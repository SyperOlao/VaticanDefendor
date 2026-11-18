from .catalog import Outcome, GameErrorCode
from .errors import ApplicationError
from .ports import UnitOfWork, UnitOfWorkFactory
from .progression_ports import PlayerProgress
from ..domain.progression import ProgressionRules, RunStats, MAX_XP, advance_level, calculate_xp


class ProgressionService:
    def pin(self, work: UnitOfWork, ruleset_id: str, content_release_id: str | None = None) -> dict:
        rules = work.progression.configuration(ruleset_id, published_only=True)
        if rules is None or content_release_id is not None and not work.progression.compatible_release(ruleset_id, content_release_id):
            raise ApplicationError(503, 'Published progression rules are unavailable', code=GameErrorCode.PROGRESSION_RULES_UNAVAILABLE)
        return rules.snapshot()

    def settle(self, work: UnitOfWork, run, stats: RunStats, outcome: str) -> dict | None:
        if run.xp_rules_snapshot is None:
            # Pre-migration runs did not pin XP rules. Do not invent a retrospective award.
            return None
        try:
            rules = ProgressionRules.from_snapshot(run.xp_rules_snapshot)
            if rules.ruleset_id != run.ruleset_id:
                raise ValueError()
        except ValueError:
            raise ApplicationError(503, 'Pinned progression rules are unavailable', code=GameErrorCode.PROGRESSION_RULES_UNAVAILABLE) from None
        xp = calculate_xp(rules, stats, outcome == Outcome.VICTORY)
        before = work.progression.progress(run.account_id)
        if before.total_xp + xp.total_xp > MAX_XP:
            raise ApplicationError(409, 'Player XP capacity exceeded', code=GameErrorCode.XP_CAPACITY_EXCEEDED)
        total = before.total_xp + xp.total_xp
        level = advance_level(rules, total, before.current_level)
        work.progression.record_xp(run.id, run.account_id, run.ruleset_id, xp)
        work.progression.save_progress(PlayerProgress(run.account_id, total, level, run.ruleset_id))
        gifts = []
        for definition in rules.levels:
            if before.current_level < definition.level <= level and definition.reward_bundle_id:
                gift = work.progression.unlock(run.account_id, rules.ruleset_id, definition.level, definition.reward_bundle_id)
                if gift:
                    gifts.append(gift.response())
        return {'rulesetId': rules.ruleset_id, 'xpGranted': xp.total_xp, 'breakdown': xp.response(),
                'totalXp': total, 'previousLevel': before.current_level, 'currentLevel': level, 'unlockedRewards': gifts}


class ProgressionUseCases:
    def __init__(self, uow: UnitOfWorkFactory):
        self.uow = uow

    def get(self, account_id):
        with self.uow() as work:
            if work.profiles.get(account_id, lock=True) is None:
                raise ApplicationError(404, 'Profile not found')
            state = work.progression.progress(account_id)
            rules = work.progression.configuration(state.last_ruleset_id) if state.last_ruleset_id else None
            next_level = next((d for d in rules.levels if d.level > state.current_level), None) if rules else None
            return {'accountId': str(account_id), 'totalXp': state.total_xp, 'currentLevel': state.current_level,
                    'rulesetId': state.last_ruleset_id,
                    'nextLevelTotalXp': next_level.total_xp_required if next_level else None,
                    'availableRewards': [gift.response() for gift in work.progression.available_gifts(account_id)]}


class ConfigureProgression:
    def __init__(self, uow: UnitOfWorkFactory):
        self.uow = uow

    def execute(self, rules: ProgressionRules):
        # Normalize ordering before comparing or publishing an immutable version.
        rules = ProgressionRules.from_snapshot(rules.snapshot())
        with self.uow() as work:
            if not work.progression.bundles_exist({d.reward_bundle_id for d in rules.levels if d.reward_bundle_id}):
                raise ApplicationError(422, 'A level reward bundle does not exist')
            if not work.progression.configure(rules):
                raise ApplicationError(409, 'Progression rules are immutable; publish a new ruleset ID')
