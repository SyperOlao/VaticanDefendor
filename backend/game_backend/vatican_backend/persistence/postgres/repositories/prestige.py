from sqlalchemy import select,func
from sqlalchemy.dialects.postgresql import insert
from ..tables import (PrestigeConfiguration,PrestigePurchase,PrestigeUpgradeDefinition,PrestigeUpgradeType,
    PlayerPrestigeUpgrade,Ruleset,OutboxMessage)
from ....application.catalog import RulesetStatus
from ....application.errors import ApplicationError
from ....domain.prestige import PAYMENT_EVENT,PurchaseStatus,payment_id,bonuses


class PrestigeRepository:
    def __init__(self,session):self.session=session

    def configuration(self,ruleset):
        row=self.session.get(PrestigeConfiguration,ruleset)
        rules=self.session.get(Ruleset,ruleset)
        return row.snapshot if row and rules.status==RulesetStatus.PUBLISHED else None

    def publish(self,rules):
        value=rules.snapshot();key=value['rulesetId']
        self.session.execute(select(func.pg_advisory_xact_lock(func.hashtextextended('prestige.configuration',0))))
        prior=self.session.get(PrestigeConfiguration,key)
        if prior:
            if prior.snapshot!=value:raise ApplicationError(409,'Prestige configuration is immutable')
            return False
        old=list(self.session.scalars(select(PrestigeUpgradeDefinition)))
        for item in value['upgrades']:
            for row in old:
                if ((row.code==item['code']) != ((row.target,row.stat)==(item['target'],item['stat']))):
                    raise ApplicationError(409,'Prestige code and bonus target cannot be reassigned')
            self.session.execute(insert(PrestigeUpgradeType).values(code=item['code']).on_conflict_do_nothing())
        self.session.execute(insert(Ruleset).values(id=key,code=key,version=1,status=RulesetStatus.PUBLISHED).on_conflict_do_nothing())
        if self.session.get(Ruleset,key).status!=RulesetStatus.PUBLISHED:raise ApplicationError(409,'Ruleset is not published')
        # Existing historical definitions are preserved, rather than silently replaced.
        existing=list(self.session.scalars(select(PrestigeUpgradeDefinition).where(PrestigeUpgradeDefinition.ruleset_id==key)))
        if existing:raise ApplicationError(409,'Legacy prestige definitions require explicit reconciliation')
        self.session.add(PrestigeConfiguration(ruleset_id=key,snapshot=value))
        for item in value['upgrades']:
            for level in item['levels']:
                self.session.add(PrestigeUpgradeDefinition(ruleset_id=key,code=item['code'],level=level['level'],
                    target=item['target'],stat=item['stat'],modifier_value=level['modifierValue'],cost=level['cost']))
        return True

    def progress(self,account,code):return self.session.get(PlayerPrestigeUpgrade,(account,code))

    def snapshot(self,account):
        rows=self.session.scalars(select(PlayerPrestigeUpgrade).where(PlayerPrestigeUpgrade.account_id==account).order_by(PlayerPrestigeUpgrade.code))
        return bonuses([r.bonus_snapshot for r in rows if r.bonus_snapshot is not None])

    def legacy(self,account):
        rows=self.session.scalars(select(PlayerPrestigeUpgrade).where(PlayerPrestigeUpgrade.account_id==account,
            PlayerPrestigeUpgrade.bonus_snapshot.is_(None)).order_by(PlayerPrestigeUpgrade.code))
        return [{'code':r.code,'level':r.level} for r in rows]

    def by_request(self,account,key):
        return self.session.scalar(select(PrestigePurchase).where(PrestigePurchase.account_id==account,PrestigePurchase.request_key==key))

    def purchase(self,account,identity):
        return self.session.scalar(select(PrestigePurchase).where(PrestigePurchase.id==identity,PrestigePurchase.account_id==account))

    def active(self,account):
        return self.session.scalar(select(PrestigePurchase).where(PrestigePurchase.account_id==account,PrestigePurchase.status==PurchaseStatus.PENDING))

    def create(self,identity,account,key,ruleset,code,before,plan):
        message=payment_id(identity)
        payload={'accountId':str(account),'purchaseId':str(identity),'code':code,'rulesetId':ruleset,
            'fromLevel':before,'toLevel':before+1,'amount':plan['cost'],'currencyId':'PRESTIGE'}
        self.session.add(OutboxMessage(id=message,destination='wallet',event_name=PAYMENT_EVENT,schema_version=1,
            aggregate_id=identity,payload=payload))
        self.session.flush()
        row=PrestigePurchase(id=identity,account_id=account,request_key=key,ruleset_id=ruleset,code=code,
            from_level=before,to_level=before+1,snapshot=plan,status=PurchaseStatus.PENDING,payment_message_id=message)
        self.session.add(row);return row

    def payment(self,purchase):return self.session.get(OutboxMessage,purchase.payment_message_id)

    def apply(self,account,plan,at):
        row=self.progress(account,plan['code'])
        if row is None:
            row=PlayerPrestigeUpgrade(account_id=account,code=plan['code'],level=plan['level'],bonus_snapshot=plan,updated_at=at)
            self.session.add(row)
        else:row.level=plan['level'];row.bonus_snapshot=plan;row.updated_at=at
