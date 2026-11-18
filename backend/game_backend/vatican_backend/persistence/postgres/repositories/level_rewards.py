from datetime import datetime
from .analytics import enqueue as enqueue_analytics
from ....domain.analytics import GIFT_ANALYTICS_EVENT
from sqlalchemy import func, select
from ....application.catalog import LevelClaimStatus
from ....application.level_reward_ports import ClaimState
from ....domain.level_rewards import GrantStatus, RewardType
from ..tables import (PlayerLevelRewardClaim, RewardBundle, RewardBundleItem, LevelRewardGrant,
                       WeaponConfiguration, CaseType)


class LevelRewardsRepository:
    def __init__(self, session):
        self.session = session

    def claim(self, account_id, claim_id):
        row = self.session.scalar(select(PlayerLevelRewardClaim).where(PlayerLevelRewardClaim.id == claim_id,
            PlayerLevelRewardClaim.account_id == account_id).with_for_update())
        if row is None:
            return None
        return ClaimState(row.id, row.account_id, row.rewarded_level, row.ruleset_id, row.reward_bundle_id,
                          row.status, row.requested_at, row.claimed_at, row.reward_snapshot)

    def list(self, account_id, limit, offset):
        rows = self.session.scalars(select(PlayerLevelRewardClaim).where(PlayerLevelRewardClaim.account_id == account_id)
            .order_by(PlayerLevelRewardClaim.rewarded_level).limit(limit).offset(offset))
        return [{'claimId': str(r.id), 'rewardedLevel': r.rewarded_level, 'rulesetId': r.ruleset_id,
                 'rewardBundleId': str(r.reward_bundle_id), 'status': r.status,
                 'requestedAt': r.requested_at.isoformat() if r.requested_at else None,
                 'claimedAt': r.claimed_at.isoformat() if r.claimed_at else None} for r in rows]

    def bundle_snapshot(self, bundle_id):
        if self.session.get(RewardBundle, bundle_id) is None:
            return None
        rows = self.session.scalars(select(RewardBundleItem).where(RewardBundleItem.bundle_id == bundle_id)
                                    .order_by(RewardBundleItem.item_index))
        return {'schemaVersion': 1, 'bundleId': str(bundle_id), 'items': [
            {'itemIndex': r.item_index, 'rewardType': r.reward_type, 'rewardRef': r.reward_ref,
             'quantity': r.quantity, **({'effect': r.effect_config} if r.effect_config is not None else {})} for r in rows]}

    def validate_references(self, lines):
        weapons = {r.reward_ref for r in lines if r.reward_type == RewardType.WEAPON}
        cases = {r.reward_ref for r in lines if r.reward_type == RewardType.CASE}
        known_weapons = set(self.session.scalars(select(WeaponConfiguration.configuration_id).where(
            WeaponConfiguration.configuration_id.in_(weapons)))) if weapons else set()
        known_cases = set(self.session.scalars(select(CaseType.code).where(CaseType.code.in_(cases)))) if cases else set()
        return weapons == known_weapons and cases == known_cases

    def grants(self, claim_id):
        rows = self.session.scalars(select(LevelRewardGrant).where(LevelRewardGrant.claim_id == claim_id)
                                    .order_by(LevelRewardGrant.item_index))
        return [{'itemIndex': r.item_index, 'rewardType': r.reward_type, 'rewardRef': r.reward_ref,
                 'quantity': r.quantity, 'status': r.status, 'messageId': str(r.message_id) if r.message_id else None,
                 'receipt': r.receipt_json, 'completedAt': r.completed_at.isoformat() if r.completed_at else None,
                 **({'effect': r.effect_config} if r.effect_config is not None else {})} for r in rows]

    def begin(self, claim_id, snapshot, at):
        row = self.session.get(PlayerLevelRewardClaim, claim_id)
        row.reward_snapshot = snapshot
        row.requested_at = at
        row.status = LevelClaimStatus.PENDING

    def add_grant(self, claim_id, bundle_id, line, message_id, receipt=None):
        self.session.add(LevelRewardGrant(claim_id=claim_id, item_index=line.item_index, bundle_id=bundle_id,
            reward_type=line.reward_type, reward_ref=line.reward_ref, quantity=line.quantity,
            effect_config=line.effect.snapshot() if line.effect else None,
            message_id=message_id, receipt_json=receipt,
            status=GrantStatus.COMPLETED if message_id is None else GrantStatus.PENDING,
            completed_at=datetime.fromisoformat(receipt['calculatedAt']) if receipt and 'calculatedAt' in receipt else None))

    def finalize(self, claim_id, at):
        row = self.session.get(PlayerLevelRewardClaim, claim_id, with_for_update=True, populate_existing=True)
        if row is None or row.status in (LevelClaimStatus.AVAILABLE, LevelClaimStatus.CLAIMED):
            return
        statuses = list(self.session.scalars(select(LevelRewardGrant.status).where(LevelRewardGrant.claim_id == claim_id)))
        if any(s == GrantStatus.FAILED for s in statuses):
            row.status = LevelClaimStatus.REVIEW
        elif statuses and all(s == GrantStatus.COMPLETED for s in statuses):
            row.status = LevelClaimStatus.CLAIMED
            row.claimed_at = at
            enqueue_analytics(self.session,row.id,GIFT_ANALYTICS_EVENT,{
                'claimId':str(row.id),'accountId':str(row.account_id),'rulesetId':row.ruleset_id,
                'rewardedLevel':row.rewarded_level,'rewardBundleId':str(row.reward_bundle_id),
                'status':LevelClaimStatus.CLAIMED,'claimedAt':at.isoformat()})
        else:
            row.status = LevelClaimStatus.PENDING


    def publish_bundle(self, code, version, plan):
        self.session.execute(select(func.pg_advisory_xact_lock(func.hashtextextended('reward.bundle:'+str(plan.bundle_id),0))))
        existing=self.session.scalar(select(RewardBundle).where(
            (RewardBundle.id==plan.bundle_id) | ((RewardBundle.code==code) & (RewardBundle.version==version))))
        if existing is not None:
            return (existing.id==plan.bundle_id and existing.code==code and existing.version==version
                    and self.bundle_snapshot(existing.id)==plan.snapshot())
        self.session.add(RewardBundle(id=plan.bundle_id,code=code,version=version))
        self.session.flush()
        self.session.add_all(RewardBundleItem(bundle_id=plan.bundle_id,item_index=line.item_index,
            reward_type=line.reward_type,reward_ref=line.reward_ref,quantity=line.quantity,
            effect_config=line.effect.snapshot() if line.effect else None) for line in plan.items)
        return True
