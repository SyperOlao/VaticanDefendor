from datetime import timedelta
from uuid import UUID,uuid4
from .ports import UnitOfWorkFactory,InventoryGateway,GamePolicy
from .catalog import RuntimeStatus,SimulationMode
from .errors import ApplicationError
from .energy import EnergyService
from .progression import ProgressionService
from .settlements import settle,delivery_response
from ..domain.clock import Clock,UtcClock
from ..domain.local_runs import LocalPolicy,LocalResult,ValidationStatus,VALIDATOR_VERSION,digest,start_digest,validate_result


def local_response(work,run,replayed=False):
    context=work.local_runs.context(run.id);submission=work.local_runs.submission(run.id)
    settlement=submission.settlement if submission else None
    return {'runId':str(run.id),'accountId':str(run.account_id),'simulationMode':SimulationMode.LOCAL,
        'status':run.status,'replayed':replayed,'contentReleaseId':run.content_release_id,'rulesetId':run.ruleset_id,
        'profileVersion':run.profile_version,'displayName':run.profile_display_name,'weapons':run.weapon_snapshot,
        'prestigeBonuses':run.prestige_snapshot or {'schemaVersion':1,'upgrades':[],'modifiers':[]},
        'sessionId':str(context.session_id) if context else None,
        'policyId':context.validation_snapshot['policyId'] if context and context.validation_snapshot else None,
        'mapId':context.map_id if context else None,'difficulty':context.difficulty if context else None,
        'startedAt':context.started_at.isoformat() if context else None,
        'submissionDeadline':context.submission_deadline.isoformat() if context else None,
        'energyCost':context.energy_cost if context else None,
        'submissionSchemaVersion':1,'validatorVersion':submission.validator_version if submission else VALIDATOR_VERSION,
        'validationStatus':submission.validation_status if submission else None,
        'validationReasons':submission.validation_reasons if submission else [],
        'settlementStatus':'COMPLETED' if settlement else 'REJECTED' if submission and submission.validation_status=='REJECTED'
            else 'PENDING' if submission else 'SKIPPED',
        'settlement':settlement,'rewardStatus':work.messages.reward_status(run.id) if settlement else 'SKIPPED',
        **delivery_response(work,run.id)}


class ConfigureLocalRuns:
    def __init__(self,uow: UnitOfWorkFactory):self.uow=uow

    def execute(self,policy: LocalPolicy):
        policy=LocalPolicy(policy.snapshot())
        with self.uow() as work:
            if work.content.get(policy['contentReleaseId']) is None:raise ApplicationError(422,'Publish content before the local policy')
            ProgressionService().pin(work,policy['rulesetId'],policy['contentReleaseId'])
            if not work.local_runs.weapon_exists(policy['rewardWeaponConfigurationId']):
                raise ApplicationError(422,'Unknown local weapon reward')
            return {'policyId':policy['policyId'],'replayed':not work.local_runs.publish(policy)}


class LocalRunUseCases:
    def __init__(self,uow: UnitOfWorkFactory,settings: GamePolicy,inventory: InventoryGateway,clock: Clock | None=None):
        self.uow,self.settings,self.inventory,self.clock=uow,settings,inventory,clock or UtcClock()
        self.energy=EnergyService(settings.energy_ruleset_id)

    @staticmethod
    def _profile(work,account):
        if work.profiles.get(account,lock=True) is None:raise ApplicationError(404,'Profile not found')

    @staticmethod
    def _owned(work,account,run_id):
        run=work.runs.get(run_id,lock=True)
        if run is None or run.account_id!=account or run.simulation_mode!=SimulationMode.LOCAL:
            raise ApplicationError(404,'Local run not found')
        return run

    @staticmethod
    def _expire(work,run,at):
        context=work.local_runs.context(run.id)
        if (run.status==RuntimeStatus.READY and context is not None and context.submission_deadline<=at
                and work.local_runs.submission(run.id) is None):
            run.status=RuntimeStatus.FAILED;run.completed_at=at

    def active(self,account):
        with self.uow() as work:
            self._profile(work,account);run=work.runs.active(account)
            if run is None or run.simulation_mode!=SimulationMode.LOCAL:return {'active':False,'run':None}
            run=self._owned(work,account,run.id);self._expire(work,run,self.clock.now())
            return {'active':run.status==RuntimeStatus.READY,'run':local_response(work,run)}

    def start(self,account,request_key,policy_id):
        if not isinstance(request_key,UUID) or not isinstance(policy_id,str):raise ApplicationError(422,'Invalid local start')
        payload_hash=start_digest(policy_id)
        with self.uow() as work:
            prior=work.local_runs.start_operation(account,request_key,payload_hash)
        if prior is not None:return self.get(account,prior,replayed=True)
        # Inventory resolves before a game transaction or account lock is acquired.
        weapons=self.inventory.snapshot(account)
        with self.uow() as work:
            self._profile(work,account)
            prior=work.local_runs.start_operation(account,request_key,payload_hash)
            if prior is not None:return local_response(work,self._owned(work,account,prior),True)
            value=work.local_runs.policy(policy_id)
            if value is None:raise ApplicationError(404,'Local policy not found')
            policy=LocalPolicy(value);at=self.clock.now()
            active=work.runs.active(account)
            if active is not None and active.simulation_mode==SimulationMode.LOCAL:self._expire(work,active,at)
            if active is not None and active.status in (RuntimeStatus.READY,RuntimeStatus.PENDING):
                raise ApplicationError(409,'Finish the active run before starting another')
            xp_rules=ProgressionService().pin(work,policy['rulesetId'],policy['contentReleaseId'])
            profile=work.profiles.get(account)
            run=work.runs.create(id=uuid4(),account_id=account,server_id=None,simulation_mode=SimulationMode.LOCAL,
                ruleset_id=policy['rulesetId'],content_release_id=policy['contentReleaseId'],generation=1,
                profile_version=profile.version,profile_display_name=profile.display_name,status=RuntimeStatus.READY,
                started_at=at,weapon_snapshot=weapons,xp_rules_snapshot=xp_rules,prestige_snapshot=work.prestige.snapshot(account),
                reward_per_church_level=policy['prestigePerChurchLevel'])
            cost=self.energy.spend(work,account,run.id,at)
            work.local_runs.create_context(run_id=run.id,account_id=account,session_id=uuid4(),client_request_key=request_key,
                map_id=policy['mapId'],difficulty=policy['difficulty'],ruleset_id=run.ruleset_id,energy_cost=cost,
                started_at=at,submission_deadline=at+timedelta(seconds=policy['submissionWindowSeconds']),
                validation_snapshot=policy.snapshot())
            work.local_runs.record_start(account,request_key,run.id,payload_hash)
            return local_response(work,run)

    def get(self,account,run_id,replayed=False):
        with self.uow() as work:
            self._profile(work,account);run=self._owned(work,account,run_id)
            self._expire(work,run,self.clock.now())
            return local_response(work,run,replayed)

    def finish(self,account,run_id,result: LocalResult):
        try:body=result.snapshot();payload_hash=digest(['local.result',1,body])
        except (ValueError,TypeError,AttributeError):raise ApplicationError(422,'Invalid local result') from None
        replayed=False
        with self.uow() as work:
            self._profile(work,account);run=self._owned(work,account,run_id)
            prior=work.local_runs.submission(run_id)
            if prior is not None:
                if prior.payload_hash!=payload_hash:raise ApplicationError(409,'Local result payload changed')
                replayed=True
                if prior.validation_status==ValidationStatus.REJECTED:return local_response(work,run,True)
            else:
                context=work.local_runs.context(run_id)
                if context is None or context.validation_snapshot is None:
                    raise ApplicationError(409,'Legacy local run has no pinned validation policy')
                at=self.clock.now()
                if at<context.started_at:raise ApplicationError(503,'Server clock is before local start')
                reasons=validate_result(LocalPolicy(context.validation_snapshot),context,body,at)
                if run.status!=RuntimeStatus.READY:reasons=sorted(set(reasons+['LocalRunInactive']))
                work.local_runs.record_submission(run,body,payload_hash,reasons,at)
                if reasons:
                    run.status=RuntimeStatus.FAILED;run.completed_at=at
                    return local_response(work,run)
        # The immutable accepted submission survives a failure in the settlement transaction.
        return self.resume(account,run_id,replayed=replayed)

    def resume(self,account,run_id,replayed=False):
        with self.uow() as work:
            self._profile(work,account);run=self._owned(work,account,run_id)
            submission=work.local_runs.submission(run_id)
            if submission is None or submission.validation_status!=ValidationStatus.ACCEPTED:
                raise ApplicationError(409,'Local result has not been accepted')
            if submission.settlement is None:
                if run.status!=RuntimeStatus.READY:raise ApplicationError(409,'Accepted local run is not ready for settlement')
                context=work.local_runs.context(run_id);policy=LocalPolicy(context.validation_snapshot)
                result=LocalResult.from_snapshot(submission.snapshot)
                submission.settlement=settle(work,run,result,submission.snapshot,submission.payload_hash,
                    max(self.clock.now(),submission.created_at),
                    rating_board=self.settings.rating_board_code,analytics=True,
                    weapon_configuration_id=policy['rewardWeaponConfigurationId'])
            else:replayed=True
            return local_response(work,run,replayed)

    def abandon(self,account,run_id):
        with self.uow() as work:
            self._profile(work,account);run=self._owned(work,account,run_id)
            if run.status==RuntimeStatus.FAILED:return local_response(work,run,True)
            if work.local_runs.submission(run_id) is not None:raise ApplicationError(409,'A submitted result cannot be abandoned')
            if run.status!=RuntimeStatus.READY:raise ApplicationError(409,'Local run is already completed')
            run.status=RuntimeStatus.FAILED;run.completed_at=run.completed_at or self.clock.now()
            return local_response(work,run)

    def maintain(self,limit=20):
        if type(limit) is not int or not 1<=limit<=100:raise ApplicationError(422,'Invalid local maintenance limit')
        with self.uow() as work:pending=work.local_runs.pending(limit);expired=work.local_runs.expired(self.clock.now(),limit)
        failed=None
        for run_id,account in pending:
            try:self.resume(account,run_id)
            except Exception as error:failed=error
        for run_id,account in expired:self.get(account,run_id)
        if failed is not None:raise failed
        return len(pending)+len(expired)
