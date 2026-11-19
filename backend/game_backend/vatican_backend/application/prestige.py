from collections import defaultdict
from hashlib import sha256
from uuid import UUID,uuid4
from ..domain.prestige import PrestigeRules,PurchaseStatus,IDENTIFIER,MAX_AMOUNT
from ..domain.clock import Clock,UtcClock
from .errors import ApplicationError
from .catalog import GameErrorCode
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
            pending_reset = work.prestige.active_reset(account)
            snapshot = work.prestige.snapshot(account)
            quote = None
            if not (active or pending_reset or legacy):
                quote, _ = self._reset_plan(work.prestige.refundable_purchases(account), snapshot)
            return {'rulesetId':key or None,'available':value is not None,'upgrades':upgrades,
                'bonuses':snapshot,'legacyUpgrades':legacy,'requiresReview':bool(legacy),
                'pendingPurchase':self._response(work,active) if active else None,
                'resetQuote':quote,'pendingReset':self._reset_response(work,pending_reset) if pending_reset else None}

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
            not_created=GameErrorCode.PRESTIGE_PURCHASE_NOT_CREATED
            if ruleset!=self.settings.prestige_ruleset_id:raise ApplicationError(409,'Prestige catalog changed',code=not_created)
            value=work.prestige.configuration(ruleset)
            if value is None:raise ApplicationError(503,'Prestige configuration unavailable')
            upgrade=PrestigeRules(value).upgrade(code)
            if upgrade is None:raise ApplicationError(404,'Prestige upgrade not found',code=not_created)
            if work.prestige.active(account) or work.prestige.active_reset(account):
                raise ApplicationError(409,'Finish the pending prestige operation first',code=not_created)
            before=work.prestige.progress(account,code)
            if before is not None and before.bonus_snapshot is None:raise ApplicationError(409,'Legacy prestige bonus requires reconciliation',code=not_created)
            level=before.level if before else 0
            if level!=expected_level:raise ApplicationError(409,'Prestige level changed',code=not_created)
            if level>=len(upgrade['levels']):raise ApplicationError(409,'Prestige maximum level reached',code=not_created)
            next_level=upgrade['levels'][level]
            if before is not None:
                from ..domain.prestige import bonus
                if bonus(next_level['modifierValue'])<=bonus(before.bonus_snapshot['modifierValue']):
                    raise ApplicationError(409,'Prestige configuration would reduce the purchased bonus',code=not_created)
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

    @staticmethod
    def _reset_plan(purchases, snapshot):
        levels = defaultdict(list)
        amount = 0
        for purchase in purchases:
            receipt = purchase.receipt or {}
            charged = receipt.get('amount')
            if receipt.get('outcome') != 'APPLIED' or type(charged) is not int or charged <= 0:
                return None, []
            amount += charged
            levels[purchase.code].append(purchase.to_level)
        expected = {upgrade['code']: list(range(1, upgrade['level'] + 1)) for upgrade in snapshot['upgrades']}
        if levels != expected or not 0 < amount <= MAX_AMOUNT:
            return None, []
        purchase_ids = sorted(str(purchase.id) for purchase in purchases)
        revision = sha256(','.join(purchase_ids).encode()).hexdigest()
        return {'revision': revision, 'amount': amount}, purchase_ids

    @staticmethod
    def _reset_response(work, row, replayed=False):
        message = work.prestige.refund(row)
        review = row.status == PurchaseStatus.PENDING and message.failed_at is not None
        return {'resetId': str(row.id), 'requestKey': str(row.request_key), 'revision': row.revision,
                'amount': message.payload['amount'], 'currencyId': 'PRESTIGE',
                'status': PurchaseStatus.REVIEW if review else row.status, 'refundMessageId': str(message.id),
                'refundReceipt': message.receipt_json, 'replayed': replayed}

    def reset(self, account, key, revision):
        if not isinstance(key, UUID) or not key.int:
            raise ApplicationError(422, 'Invalid prestige reset request key')
        with self.uow() as work:
            self._lock(work, account)
            prior = work.prestige.reset_by_request(account, key)
            if prior:
                if prior.revision != revision:
                    raise ApplicationError(409, 'Prestige reset payload changed')
                return self._reset_response(work, prior, True)
            quote, purchase_ids = self._reset_plan(
                work.prestige.refundable_purchases(account), work.prestige.snapshot(account))
            if (quote is None or quote['revision'] != revision or work.prestige.active(account)
                    or work.prestige.active_reset(account) or work.prestige.legacy(account)):
                raise ApplicationError(409, 'Prestige reset quote is no longer available',
                                       code=GameErrorCode.PRESTIGE_RESET_NOT_CREATED)
            row = work.prestige.create_reset(uuid4(), account, key, quote, purchase_ids)
            return self._reset_response(work, row)

    def get_reset(self, account, identity):
        with self.uow() as work:
            self._lock(work, account)
            row = work.prestige.reset(account, identity)
            if row is None:
                raise ApplicationError(404, 'Prestige reset not found')
            return self._reset_response(work, row)

    def settle_reset(self, work, message, receipt):
        account = UUID(message['payload']['accountId'])
        self._lock(work, account)
        row = work.prestige.reset(account, UUID(message['payload']['resetId']))
        if row is None or row.refund_message_id != message['id']:
            raise ApplicationError(409, 'Prestige refund source changed')
        if row.status != PurchaseStatus.PENDING:
            return
        if receipt['outcome'] == 'REJECTED':
            row.status = PurchaseStatus.REJECTED
            return
        purchases = work.prestige.refundable_purchases(account)
        quote, purchase_ids = self._reset_plan(purchases, work.prestige.snapshot(account))
        if quote is None or quote['revision'] != row.revision or purchase_ids != message['payload']['purchaseIds']:
            raise ApplicationError(409, 'Prestige progress changed during refund')
        work.prestige.apply_reset(row, purchases)
        work.profiles.bump(account)
        row.status = PurchaseStatus.COMPLETED
