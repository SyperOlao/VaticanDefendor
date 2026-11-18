from uuid import UUID
from sqlalchemy import select,func
from ..tables import (LocalRunPolicy,RunContext,RunResultSubmission,RunValidationAudit,Run,
                      IdempotencyRecord,WeaponConfiguration)
from ....application.errors import ApplicationError
from ....application.catalog import SimulationMode,RuntimeStatus,RewardStatus
from ....domain.local_runs import VALIDATOR_VERSION


class LocalRunsRepository:
    def __init__(self,session):self.session=session

    def policy(self,identity):
        row=self.session.get(LocalRunPolicy,identity)
        return row.snapshot if row else None

    def publish(self,policy):
        value=policy.snapshot()
        self.session.execute(select(func.pg_advisory_xact_lock(func.hashtextextended('local-policy:'+value['policyId'],0))))
        prior=self.session.get(LocalRunPolicy,value['policyId'])
        if prior is not None:
            if prior.snapshot!=value:raise ApplicationError(409,'Local policies are immutable')
            return False
        self.session.add(LocalRunPolicy(id=value['policyId'],ruleset_id=value['rulesetId'],
            content_release_id=value['contentReleaseId'],snapshot=value))
        return True

    def weapon_exists(self,key):return self.session.get(WeaponConfiguration,key) is not None

    def start_operation(self,account,key,digest):
        row=self.session.get(IdempotencyRecord,(account,'run.start',str(key)))
        if row is None:return None
        if row.payload_hash!=digest:raise ApplicationError(409,'Start request payload or mode changed')
        return UUID(row.result_reference)

    def record_start(self,account,key,run_id,digest):
        self.session.add(IdempotencyRecord(account_id=account,operation='run.start',request_key=str(key),
            payload_hash=digest,status=RewardStatus.COMPLETED,result_reference=str(run_id),
            result_json={'runId':str(run_id),'simulationMode':'LOCAL'},completed_at=func.now()))

    def context(self,run_id):return self.session.get(RunContext,run_id)
    def create_context(self,**values):self.session.add(RunContext(**values))
    def submission(self,run_id):return self.session.get(RunResultSubmission,run_id)

    def record_submission(self,run,body,digest,reasons,at):
        status='REJECTED' if reasons else 'ACCEPTED'
        self.session.add(RunResultSubmission(run_id=run.id,payload_hash=digest,schema_version=1,snapshot=body,
            validation_status=status,validator_version=VALIDATOR_VERSION,validation_reasons=reasons,created_at=at))
        self.session.flush()
        self.session.add(RunValidationAudit(run_id=run.id,ruleset_id=run.ruleset_id,
            validator_version=VALIDATOR_VERSION,status=status,reasons=reasons,created_at=at))

    def pending(self,limit):
        return list(self.session.execute(select(Run.id,Run.account_id).join(RunResultSubmission,Run.id==RunResultSubmission.run_id)
            .where(Run.simulation_mode==SimulationMode.LOCAL,Run.status==RuntimeStatus.READY,
                   RunResultSubmission.validation_status=='ACCEPTED',RunResultSubmission.settlement.is_(None))
            .order_by(RunResultSubmission.created_at,Run.id).limit(limit)).tuples())

    def expired(self,at,limit):
        return list(self.session.execute(select(Run.id,Run.account_id).join(RunContext,Run.id==RunContext.run_id)
            .where(Run.simulation_mode==SimulationMode.LOCAL,Run.status==RuntimeStatus.READY,
                   RunContext.submission_deadline<=at,~select(RunResultSubmission.run_id).where(RunResultSubmission.run_id==Run.id).exists())
            .order_by(RunContext.submission_deadline,Run.id).limit(limit)).tuples())
