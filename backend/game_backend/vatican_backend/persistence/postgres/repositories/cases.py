from sqlalchemy import func, select
from .analytics import enqueue as enqueue_analytics
from ....domain.analytics import CASE_ANALYTICS_EVENT
from sqlalchemy.dialects.postgresql import insert
from ....application.case_ports import Opening
from ....application.catalog import RulesetStatus
from ....domain.cases import CaseRules, OpeningStatus, funding_id
from ....domain.level_rewards import GrantStatus
from .level_rewards import LevelRewardsRepository
from ..tables import (CaseDefinition, CaseDropEntry, CaseOpening, CaseRewardGrant, CaseType, PlayerCasePity, Ruleset)


class CasesRepository:
    def __init__(self, session):
        self.session = session

    def catalog(self, limit, offset):
        rows = self.session.scalars(select(CaseDefinition).join(Ruleset,Ruleset.id==CaseDefinition.ruleset_id)
            .where(Ruleset.status==RulesetStatus.PUBLISHED).order_by(CaseDefinition.code,CaseDefinition.id).limit(limit).offset(offset))
        return [{'caseId':str(r.id),'code':r.code,'rulesetId':r.ruleset_id,'priceCurrency':r.price_currency,
                 'price':r.price,'pityThreshold':r.pity_threshold,'guaranteedRarity':r.guaranteed_rarity} for r in rows]

    def definition(self, case_id, published_only=True):
        row = self.session.get(CaseDefinition,case_id)
        if row is None or published_only and self.session.get(Ruleset,row.ruleset_id).status != RulesetStatus.PUBLISHED:
            return None
        rewards = LevelRewardsRepository(self.session)
        drops = self.session.scalars(select(CaseDropEntry).where(CaseDropEntry.case_id==case_id).order_by(CaseDropEntry.entry_index))
        return CaseRules.parse({'schemaVersion':1,'caseId':str(row.id),'code':row.code,'rulesetId':row.ruleset_id,
            'priceCurrency':row.price_currency,'price':row.price,'pityThreshold':row.pity_threshold,
            'guaranteedRarity':row.guaranteed_rarity,'drops':[{'entryIndex':d.entry_index,'rarity':d.rarity,
                'weight':d.weight,'reward':rewards.bundle_snapshot(d.reward_bundle_id)} for d in drops]})

    def publish(self, rules):
        # Lock ID and semantic version in a consistent order, including competing IDs.
        for key in sorted({'case.id:'+str(rules.id),'case.version:'+rules.code+':'+rules.ruleset_id}):
            self.session.execute(select(func.pg_advisory_xact_lock(func.hashtextextended(key,0))))
        policy = self.session.get(Ruleset,rules.ruleset_id)
        if policy is None or policy.status != RulesetStatus.PUBLISHED:
            return False
        rewards = LevelRewardsRepository(self.session)
        for drop in rules.drops:
            if rewards.bundle_snapshot(drop.plan.bundle_id) != drop.plan.snapshot() or not rewards.validate_references(drop.plan.items):
                return False
        existing = self.session.scalar(select(CaseDefinition).where((CaseDefinition.id==rules.id) | (
            (CaseDefinition.code==rules.code) & (CaseDefinition.ruleset_id==rules.ruleset_id))))
        if existing:
            return existing.id==rules.id and self.definition(existing.id).snapshot()==rules.snapshot()
        self.session.execute(insert(CaseType).values(code=rules.code).on_conflict_do_nothing())
        self.session.add(CaseDefinition(id=rules.id,code=rules.code,ruleset_id=rules.ruleset_id,
            price_currency=rules.currency,price=rules.price,pity_threshold=rules.pity_threshold,guaranteed_rarity=rules.guaranteed_rarity))
        self.session.flush()
        self.session.add_all(CaseDropEntry(case_id=rules.id,entry_index=d.index,reward_bundle_id=d.plan.bundle_id,
                                          rarity=d.rarity,weight=d.weight) for d in rules.drops)
        return True

    @staticmethod
    def _opening(row):
        return Opening(row.id,row.account_id,row.case_id,row.case_code,row.client_request_key,row.status,
            row.payment_mode,row.random_seed,row.definition_snapshot,row.result,row.payment_message_id,row.payment_receipt,row.completed_at) if row else None

    def by_request(self, account_id, key):
        return self._opening(self.session.scalar(select(CaseOpening).where(CaseOpening.account_id==account_id,
            CaseOpening.client_request_key==key).with_for_update()))

    def opening(self, account_id, opening_id):
        return self._opening(self.session.scalar(select(CaseOpening).where(CaseOpening.account_id==account_id,
            CaseOpening.id==opening_id).with_for_update()))

    def active(self, account_id, code):
        return self.session.scalar(select(CaseOpening.id).where(CaseOpening.account_id==account_id,
            CaseOpening.case_code==code,CaseOpening.status.in_([OpeningStatus.FUNDING,OpeningStatus.PENDING,OpeningStatus.REVIEW])).limit(1)) is not None

    def create(self, opening_id, account_id, key, rules, mode, seed):
        self.session.add(CaseOpening(id=opening_id,account_id=account_id,case_id=rules.id,case_code=rules.code,
            client_request_key=key,status=OpeningStatus.FUNDING,payment_mode=mode,definition_snapshot=rules.snapshot(),
            result={},random_seed=seed,payment_message_id=funding_id(opening_id)))
        self.session.flush()

    def grants(self, opening_id):
        rows = self.session.scalars(select(CaseRewardGrant).where(CaseRewardGrant.opening_id==opening_id).order_by(CaseRewardGrant.item_index))
        return [{'itemIndex':r.item_index,'rewardType':r.reward_type,'rewardRef':r.reward_ref,'quantity':r.quantity,
                 'status':r.status,'messageId':str(r.message_id) if r.message_id else None,'receipt':r.receipt_json,
                 'completedAt':r.completed_at.isoformat() if r.completed_at else None,
                 **({'effect':r.effect_config} if r.effect_config is not None else {})} for r in rows]

    def pity(self, account_id, code):
        row = self.session.get(PlayerCasePity,(account_id,code))
        return row.rolls_since_guarantee if row else 0

    def record_draw(self, opening_id, rules, drop, before, after, guaranteed, roll, total, receipt):
        row = self.session.get(CaseOpening,opening_id)
        row.reward_bundle_id, row.rarity, row.pity_before, row.pity_after = drop.plan.bundle_id, drop.rarity, before, after
        row.result = {'entryIndex':drop.index,'rarity':drop.rarity,'rewardBundleId':str(drop.plan.bundle_id),
            'pityBefore':before,'pityAfter':after,'guaranteedTriggered':guaranteed,'roll':roll,'totalWeight':total,
            'reward':drop.plan.snapshot()}
        row.payment_receipt = receipt
        row.status = OpeningStatus.PENDING
        pity = self.session.get(PlayerCasePity,(row.account_id,rules.code))
        if pity:
            pity.rolls_since_guarantee, pity.version = after, pity.version+1
        else:
            self.session.add(PlayerCasePity(account_id=row.account_id,case_code=rules.code,rolls_since_guarantee=after,version=1))
        self.session.flush()

    def reject(self, opening_id, receipt, at):
        row = self.session.get(CaseOpening,opening_id)
        row.payment_receipt, row.status, row.completed_at = receipt, OpeningStatus.REJECTED, at

    def add_grant(self, opening_id, bundle_id, line, message_id, receipt, at):
        self.session.add(CaseRewardGrant(opening_id=opening_id,bundle_id=bundle_id,item_index=line.item_index,
            reward_type=line.reward_type,reward_ref=line.reward_ref,quantity=line.quantity,
            effect_config=line.effect.snapshot() if line.effect else None,message_id=message_id,
            status=GrantStatus.PENDING if message_id else GrantStatus.COMPLETED,
            receipt_json=receipt,completed_at=None if message_id else at))

    def finalize(self, opening_id, at):
        row = self.session.get(CaseOpening,opening_id,with_for_update=True,populate_existing=True)
        if row is None or row.status in (OpeningStatus.COMPLETED,OpeningStatus.REJECTED) or not row.result:
            return
        statuses = list(self.session.scalars(select(CaseRewardGrant.status).where(CaseRewardGrant.opening_id==opening_id)))
        if any(s==GrantStatus.FAILED for s in statuses):
            row.status = OpeningStatus.REVIEW
        elif statuses and all(s==GrantStatus.COMPLETED for s in statuses):
            row.status, row.completed_at = OpeningStatus.COMPLETED, at
            enqueue_analytics(self.session,row.id,CASE_ANALYTICS_EVENT,{
                'openingId':str(row.id),'accountId':str(row.account_id),'caseId':str(row.case_id),'caseCode':row.case_code,
                'rulesetId':row.definition_snapshot['rulesetId'],'paymentMode':row.payment_mode,
                'rewardBundleId':str(row.reward_bundle_id),'rarity':row.rarity,'pityBefore':row.pity_before,
                'pityAfter':row.pity_after,'guaranteedTriggered':row.result['guaranteedTriggered'],
                'entryIndex':row.result['entryIndex'],'status':OpeningStatus.COMPLETED,'completedAt':at.isoformat()})
        else:
            row.status = OpeningStatus.PENDING
