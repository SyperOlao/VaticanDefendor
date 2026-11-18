import secrets
from uuid import UUID, uuid4
from ..domain.cases import CaseRules, OpeningStatus, PaymentMode, CASE_CONSUME, CASE_PAYMENT, CASE_REWARD, seeded_randbelow
from ..domain.clock import Clock, UtcClock
from ..domain.analytics import CASE_ANALYTICS_EVENT
from ..domain.level_rewards import RewardType, GrantStatus
from .energy import EnergyService
from .errors import ApplicationError
from .ports import UnitOfWorkFactory


class CaseUseCases:
    def __init__(self, uow: UnitOfWorkFactory, energy: EnergyService, clock: Clock | None = None, randbelow=None):
        self.uow, self.energy, self.clock = uow, energy, clock or UtcClock()
        self.randbelow = randbelow

    @staticmethod
    def _lock(work, account_id):
        if work.profiles.get(account_id,lock=True) is None:
            raise ApplicationError(404,'Profile not found')

    def _response(self, work, opening, replayed=False):
        lines = work.cases.grants(opening.id)
        partial = any(line['status']==GrantStatus.COMPLETED for line in lines)
        analytics=work.messages.analytics_delivery(opening.id,CASE_ANALYTICS_EVENT)
        return {'analyticsStatus':analytics['status'],'analytics':analytics,'openingId':str(opening.id),'caseId':str(opening.case_id),'caseCode':opening.case_code,
            'requestKey':str(opening.request_key),'paymentMode':opening.payment_mode,'status':opening.status,
            'deliveryStatus':'REVIEW' if opening.status==OpeningStatus.REVIEW else 'PARTIAL' if partial and opening.status==OpeningStatus.PENDING else opening.status,
            'requiresReview':opening.status==OpeningStatus.REVIEW,'paymentMessageId':str(opening.payment_message_id) if opening.payment_message_id else None,
            'paymentReceipt':opening.payment_receipt,'result':opening.result or None,'lines':lines,
            'completedAt':opening.completed_at.isoformat() if opening.completed_at else None,'replayed':replayed}

    def catalog(self, limit=50, offset=0):
        if type(limit) is not int or not 1 <= limit <= 100 or type(offset) is not int or not 0 <= offset <= 100000:
            raise ApplicationError(422,'Invalid case page')
        with self.uow() as work:
            return {'cases':work.cases.catalog(limit,offset),'limit':limit,'offset':offset}

    def get(self, account_id, opening_id):
        with self.uow() as work:
            self._lock(work,account_id)
            opening = work.cases.opening(account_id,opening_id)
            if opening is None:
                raise ApplicationError(404,'Case opening not found')
            return self._response(work,opening)

    def open(self, account_id, case_id, request_key, payment_mode=PaymentMode.INVENTORY):
        if not isinstance(request_key,UUID) or not isinstance(case_id,UUID) or not isinstance(payment_mode,str) or payment_mode not in set(PaymentMode):
            raise ApplicationError(422,'Invalid case opening request')
        with self.uow() as work:
            self._lock(work,account_id)
            prior = work.cases.by_request(account_id,request_key)
            if prior:
                if prior.case_id != case_id or prior.payment_mode != payment_mode:
                    raise ApplicationError(409,'Case opening request payload changed')
                return self._response(work,prior,True)
            try:
                rules = work.cases.definition(case_id)
            except ValueError:
                raise ApplicationError(422,'Case configuration is invalid') from None
            if rules is None:
                raise ApplicationError(404,'Published case not found')
            if not all(work.level_rewards.validate_references(d.plan.items) for d in rules.drops):
                raise ApplicationError(422,'Case reward content is unavailable')
            if any(line.reward_type in (RewardType.ENERGY,RewardType.EFFECT) for d in rules.drops for line in d.plan.items):
                # Verify local rules before any remote debit. They may later become unavailable;
                # a successful debit then remains resumable via the same frozen operation.
                self.energy.refresh(work,account_id,self.clock.now())
            if work.cases.active(account_id,rules.code):
                raise ApplicationError(409,'Another opening of this case is still pending')
            opening_id = uuid4()
            payload = {'accountId':str(account_id),'openingId':str(opening_id),'caseCode':rules.code}
            if payment_mode == PaymentMode.INVENTORY:
                event, destination = CASE_CONSUME, 'inventory'
                payload['quantity'] = 1
            else:
                if rules.price <= 0:
                    raise ApplicationError(422,'This case has no wallet purchase price')
                event, destination = CASE_PAYMENT, 'wallet'
                payload.update(currencyId=rules.currency,amount=rules.price)
            work.messages.enqueue_case(opening_id,destination,event,payload)
            work.cases.create(opening_id,account_id,request_key,rules,payment_mode,secrets.token_bytes(32))
            return self._response(work,work.cases.opening(account_id,opening_id))

    def settle(self, work, message, receipt):
        """Called in the same transaction as the lease-guarded funding confirmation."""
        account, identity = UUID(message['payload']['accountId']), UUID(message['payload']['openingId'])
        self._lock(work,account)
        opening = work.cases.opening(account,identity)
        if opening is None or opening.result or opening.status in (OpeningStatus.COMPLETED,OpeningStatus.REJECTED):
            return
        at = self.clock.now()
        if receipt['outcome']=='REJECTED':
            work.cases.reject(identity,receipt,at)
            return
        rules = CaseRules.parse(opening.snapshot)
        before = work.cases.pity(account,rules.code)
        drop, after, guaranteed, roll, total = rules.draw(before,self.randbelow or (lambda upper: seeded_randbelow(opening.random_seed,upper)))
        work.cases.record_draw(identity,rules,drop,before,after,guaranteed,roll,total,receipt)
        for line in drop.plan.items:
            result, message_id = None, None
            payload = {'accountId':str(account),'openingId':str(identity),**line.response()}
            if line.reward_type == RewardType.ENERGY:
                result = self.energy.credit(work,account,line.quantity,at)
            elif line.reward_type == RewardType.EFFECT:
                effect = self.energy.grant_regen_effect(work,account,f'case-reward:{identity}:{line.item_index}',
                    line.effect.modifier_value,line.effect.duration_seconds,at)
                result = {**payload,'effectId':str(effect.id),'effectType':effect.effect_type,
                    'modifierValue':format(effect.modifier_value,'.6f'),'sourceKey':effect.source_key,
                    'startsAt':effect.starts_at.isoformat(),'expiresAt':effect.expires_at.isoformat()}
            else:
                destination = 'wallet' if line.reward_type==RewardType.CURRENCY else 'inventory'
                message_id = work.messages.enqueue_case(identity,destination,CASE_REWARD,payload,line.item_index)
            work.cases.add_grant(identity,drop.plan.bundle_id,line,message_id,result,at)
        work.cases.finalize(identity,at)


class PublishCase:
    def __init__(self, uow: UnitOfWorkFactory):
        self.uow = uow

    def execute(self, rules: CaseRules):
        rules = CaseRules.parse(rules.snapshot())
        with self.uow() as work:
            if not work.cases.publish(rules):
                raise ApplicationError(409,'Case version is immutable or referenced published content is unavailable')
