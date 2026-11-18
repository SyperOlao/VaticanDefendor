from ..domain.clock import Clock, UtcClock
from ..domain.analytics import GIFT_ANALYTICS_EVENT
from ..domain.level_rewards import DeliveryStatus, GrantStatus, RewardPlan, RewardType
from .catalog import GameErrorCode, LevelClaimStatus
from .energy import EnergyService
from .errors import ApplicationError
from .ports import UnitOfWorkFactory


class LevelRewardsUseCases:
    def __init__(self, uow: UnitOfWorkFactory, energy: EnergyService, clock: Clock | None = None):
        self.uow, self.energy, self.clock = uow, energy, clock or UtcClock()

    def bundle(self, bundle_id):
        with self.uow() as work:
            snapshot = work.level_rewards.bundle_snapshot(bundle_id)
            if snapshot is None:
                raise ApplicationError(404, 'Reward bundle not found')
            try:
                plan = RewardPlan.parse(snapshot)
            except (ValueError, TypeError):
                raise ApplicationError(422, 'Reward bundle is invalid') from None
            if not work.level_rewards.validate_references(plan.items):
                raise ApplicationError(422, 'Reward bundle references unknown content')
            return plan.snapshot()

    @staticmethod
    def _lock(work, account_id):
        if work.profiles.get(account_id, lock=True) is None:
            raise ApplicationError(404, 'Profile not found')

    def _response(self, work, claim, replayed=False):
        grants = work.level_rewards.grants(claim.id)
        snapshot = claim.snapshot or work.level_rewards.bundle_snapshot(claim.bundle_id)
        if claim.status == LevelClaimStatus.AVAILABLE:
            saved = {g['itemIndex']: g for g in grants}
            lines = [{**line, 'status': LevelClaimStatus.AVAILABLE, 'messageId': None, 'receipt': None, 'completedAt': None}
                     if line['itemIndex'] not in saved else saved[line['itemIndex']]
                     for line in (snapshot or {}).get('items', [])]
        else:
            lines = grants
        completed = sum(line['status'] == GrantStatus.COMPLETED for line in lines)
        delivery = (DeliveryStatus.COMPLETED if claim.status == LevelClaimStatus.CLAIMED else
                    DeliveryStatus.REVIEW if claim.status == LevelClaimStatus.REVIEW else
                    DeliveryStatus.PARTIAL if completed else claim.status)
        analytics=work.messages.analytics_delivery(claim.id,GIFT_ANALYTICS_EVENT)
        return {'analyticsStatus':analytics['status'],'analytics':analytics,'claimId': str(claim.id), 'rewardedLevel': claim.rewarded_level,
                'rulesetId': claim.ruleset_id, 'rewardBundleId': str(claim.bundle_id),
                'status': claim.status, 'deliveryStatus': delivery, 'requiresReview': claim.status == LevelClaimStatus.REVIEW,
                'requestedAt': claim.requested_at.isoformat() if claim.requested_at else None,
                'claimedAt': claim.claimed_at.isoformat() if claim.claimed_at else None,
                'lines': lines, 'replayed': replayed}

    def get(self, account_id, claim_id):
        with self.uow() as work:
            self._lock(work, account_id)
            claim = work.level_rewards.claim(account_id, claim_id)
            if claim is None:
                raise ApplicationError(404, 'Level reward not found')
            return self._response(work, claim)

    def list(self, account_id, limit=50, offset=0):
        if type(limit) is not int or not 1 <= limit <= 100 or type(offset) is not int or not 0 <= offset <= 100000:
            raise ApplicationError(422, 'Invalid reward page')
        with self.uow() as work:
            self._lock(work, account_id)
            return {'claims': work.level_rewards.list(account_id, limit, offset), 'limit': limit, 'offset': offset}

    def claim(self, account_id, claim_id):
        with self.uow() as work:
            self._lock(work, account_id)
            claim = work.level_rewards.claim(account_id, claim_id)
            if claim is None:
                raise ApplicationError(404, 'Level reward not found')
            if claim.status != LevelClaimStatus.AVAILABLE:
                return self._response(work, claim, replayed=True)
            try:
                plan = RewardPlan.parse(claim.snapshot or work.level_rewards.bundle_snapshot(claim.bundle_id))
                if plan.bundle_id != claim.bundle_id:
                    raise ValueError()
            except ValueError:
                raise ApplicationError(422, 'Reward bundle is empty, invalid or unsupported',
                                       code=GameErrorCode.INVALID_REWARD_BUNDLE) from None
            if not work.level_rewards.validate_references(plan.items):
                raise ApplicationError(422, 'Reward bundle references unknown content', code=GameErrorCode.INVALID_REWARD_BUNDLE)
            previous = {row['itemIndex']: row for row in work.level_rewards.grants(claim.id)}
            if not set(previous) <= {line.item_index for line in plan.items}:
                raise ApplicationError(409, 'Existing reward grants are not part of this bundle')
            for line in plan.items:
                prior = previous.get(line.item_index)
                if prior and (prior['status'] != GrantStatus.COMPLETED or any(prior[k] != v for k, v in line.response().items())):
                    raise ApplicationError(409, 'Existing reward grant conflicts with the saved bundle')
            at = self.clock.now()
            work.level_rewards.begin(claim.id, plan.snapshot(), at)
            for line in plan.items:
                if line.item_index in previous:
                    continue  # An imported historical grant is proof of an already applied line.
                if line.reward_type == RewardType.ENERGY:
                    receipt = self.energy.credit(work, account_id, line.quantity, at)
                    work.level_rewards.add_grant(claim.id, plan.bundle_id, line, None, receipt)
                elif line.reward_type == RewardType.EFFECT:
                    effect = self.energy.grant_regen_effect(work, account_id,
                        f'level-reward:{claim.id}:{line.item_index}', line.effect.modifier_value,
                        line.effect.duration_seconds, at)
                    receipt = {**line.payload(account_id, claim.id), 'effectId': str(effect.id),
                               'sourceKey': effect.source_key, 'effectType': effect.effect_type,
                               'modifierValue': format(effect.modifier_value, '.6f'),
                               'startsAt': effect.starts_at.isoformat(), 'expiresAt': effect.expires_at.isoformat(),
                               'calculatedAt': effect.starts_at.isoformat()}
                    work.level_rewards.add_grant(claim.id, plan.bundle_id, line, None, receipt)
                else:
                    message = work.messages.enqueue_level_reward(account_id, claim.id, line)
                    work.level_rewards.add_grant(claim.id, plan.bundle_id, line, message)
            work.level_rewards.finalize(claim.id, at)
            return self._response(work, work.level_rewards.claim(account_id, claim_id))


class PublishRewardBundle:
    def __init__(self, uow: UnitOfWorkFactory):
        self.uow = uow

    def execute(self, code, version, plan: RewardPlan):
        import re
        if (not isinstance(code,str) or re.fullmatch(r'[A-Za-z0-9_.-]{1,64}',code) is None
                or type(version) is not int or not 1 <= version <= 2_147_483_647):
            raise ApplicationError(422,'Invalid reward bundle version')
        plan=RewardPlan.parse(plan.snapshot())
        with self.uow() as work:
            if not work.level_rewards.validate_references(plan.items):
                raise ApplicationError(422,'Reward bundle references unknown content')
            if not work.level_rewards.publish_bundle(code,version,plan):
                raise ApplicationError(409,'Reward bundle is immutable; use a new version and ID')
