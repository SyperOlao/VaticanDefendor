from uuid import UUID,uuid4
from ..domain.prestige import PrestigeRules,PurchaseStatus,IDENTIFIER
from ..domain.clock import Clock,UtcClock
from .errors import ApplicationError
from .ports import UnitOfWorkFactory,GamePolicy


class PublishPrestige:
    def __init__(self,uow: UnitOfWorkFactory):self.uow=uow
    def execute(self,rules: PrestigeRules):
        rules=PrestigeRules(rules.snapshot())
        with self.uow() as work:return {'rulesetId':rules.snapshot()['rulesetId'],'replayed':not work.prestige.publish(rules)}


class PrestigeUseCases:
    def __init__(self,uow: UnitOfWorkFactory,settings: GamePolicy,clock: Clock|None=None):
        self.uow,self.settings,self.clock=uow,settings,clock or UtcClock()

    @staticmethod
    def _lock(work,account):
        if work.profiles.get(account,lock=True) is None:raise ApplicationError(404,'Profile not found')

    @staticmethod
    def _response(work,row,replayed=False):
        message=work.prestige.payment(row)
        review=row.status==PurchaseStatus.PENDING and message.failed_at is not None
        return {'purchaseId':str(row.id),'requestKey':str(row.request_key),'rulesetId':row.ruleset_id,'code':row.code,
            'fromLevel':row.from_level,'toLevel':row.to_level,'cost':row.snapshot['cost'],'currencyId':'PRESTIGE',
            'status':PurchaseStatus.REVIEW if review else row.status,'paymentMessageId':str(row.payment_message_id),
            'attempts':message.attempts,'lastError':message.last_error,'paymentReceipt':row.receipt,
            'result':row.result,'completedAt':row.completed_at.isoformat() if row.completed_at else None,'replayed':replayed}

    def catalog(self,account):
        with self.uow() as work:
            self._lock(work,account)
            key=self.settings.prestige_ruleset_id
            value=work.prestige.configuration(key) if key else None
            upgrades=[]
            if value:
                rules=PrestigeRules(value)
                for item in rules.snapshot()['upgrades']:
                    progress=work.prestige.progress(account,item['code']);level=progress.level if progress else 0
                    upgrades.append({**item,'currentLevel':level,'maxLevel':len(item['levels']),
                        'requiresReview':progress is not None and progress.bonus_snapshot is None,
                        'nextLevel':next((i for i in item['levels'] if i['level']==level+1),None)})
            active=work.prestige.active(account);legacy=work.prestige.legacy(account)
            return {'rulesetId':key or None,'available':value is not None,'upgrades':upgrades,
                'bonuses':work.prestige.snapshot(account),'legacyUpgrades':legacy,'requiresReview':bool(legacy),'pendingPurchase':self._response(work,active) if active else None}

    def get(self,account,identity):
        with self.uow() as work:
            self._lock(work,account);row=work.prestige.purchase(account,identity)
            if row is None:raise ApplicationError(404,'Prestige purchase not found')
            return self._response(work,row)

    def buy(self,account,key,ruleset,code,expected_level):
        if (not isinstance(key,UUID) or not isinstance(code,str) or IDENTIFIER.fullmatch(code) is None
                or not isinstance(ruleset,str) or type(expected_level) is not int or not 0<=expected_level<=5):
            raise ApplicationError(422,'Invalid prestige purchase')
        with self.uow() as work:
            self._lock(work,account)
            prior=work.prestige.by_request(account,key)
            if prior:
                if (prior.ruleset_id,prior.code,prior.from_level)!=(ruleset,code,expected_level):raise ApplicationError(409,'Prestige request payload changed')
                return self._response(work,prior,True)
            if ruleset!=self.settings.prestige_ruleset_id:raise ApplicationError(409,'Prestige catalog changed')
            value=work.prestige.configuration(ruleset)
            if value is None:raise ApplicationError(503,'Prestige configuration unavailable')
            upgrade=PrestigeRules(value).upgrade(code)
            if upgrade is None:raise ApplicationError(404,'Prestige upgrade not found')
            if work.prestige.active(account):raise ApplicationError(409,'Finish the pending prestige purchase first')
            before=work.prestige.progress(account,code)
            if before is not None and before.bonus_snapshot is None:raise ApplicationError(409,'Legacy prestige bonus requires reconciliation')
            level=before.level if before else 0
            if level!=expected_level:raise ApplicationError(409,'Prestige level changed')
            if level>=len(upgrade['levels']):raise ApplicationError(409,'Prestige maximum level reached')
            next_level=upgrade['levels'][level]
            if before is not None:
                from ..domain.prestige import bonus
                if bonus(next_level['modifierValue'])<=bonus(before.bonus_snapshot['modifierValue']):
                    raise ApplicationError(409,'Prestige configuration would reduce the purchased bonus')
            plan={'rulesetId':ruleset,'code':code,'target':upgrade['target'],'stat':upgrade['stat'],**next_level}
            row=work.prestige.create(uuid4(),account,key,ruleset,code,level,plan)
            return self._response(work,row)

    def settle(self,work,message,receipt):
        account=UUID(message['payload']['accountId']);identity=UUID(message['payload']['purchaseId'])
        self._lock(work,account);row=work.prestige.purchase(account,identity)
        if row is None:raise ApplicationError(409,'Prestige purchase missing')
        if row.status!=PurchaseStatus.PENDING:return
        if row.payment_message_id!=message['id']:raise ApplicationError(409,'Prestige payment source changed')
        at=self.clock.now()
        if receipt['outcome']=='REJECTED':row.status=PurchaseStatus.REJECTED
        else:
            progress=work.prestige.progress(account,row.code)
            if (progress.level if progress else 0)!=row.from_level:raise ApplicationError(409,'Prestige progress changed during payment')
            work.prestige.apply(account,row.snapshot,at)
            profile=work.profiles.bump(account)
            row.result={'code':row.code,'level':row.to_level,'bonus':row.snapshot,'profileVersion':profile.version}
            row.status=PurchaseStatus.COMPLETED
        row.receipt=receipt;row.completed_at=at
