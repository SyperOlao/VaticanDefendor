from ....application.catalog import RewardStatus, RulesetStatus, RuntimeStatus
from datetime import timedelta
import hashlib
from uuid import UUID

from sqlalchemy import func, select
from sqlalchemy.dialects.postgresql import insert

from ..tables import ContentRelease, GameServer, Ruleset, Run, RunCheckpoint, RunResultOperation, IdempotencyRecord, RunValidationAudit


class RunsRepository:
    def __init__(self, session):
        self.session = session

    def server(self, server_id, lock=False):
        return self.session.get(GameServer, server_id, with_for_update=lock, populate_existing=lock)

    def register_server(self, **values):
        statement = insert(GameServer).values(**values)
        # Only an unassigned process can be registered, including concurrent first registrations.
        changed = {key: getattr(statement.excluded, key) for key in values if key != "id"}
        return self.session.scalar(statement.on_conflict_do_update(
            index_elements=[GameServer.id], set_=changed, where=GameServer.current_run_id.is_(None)
        ).returning(GameServer.id))

    def ensure_ruleset(self, release_id):
        release = self.session.get(ContentRelease, release_id)
        self.session.execute(insert(Ruleset).values(
            id=release_id, code=release_id, version=1, status=RulesetStatus.PUBLISHED,
            content_release_id=release_id if release is not None else None,
        ).on_conflict_do_nothing(index_elements=[Ruleset.id]))

    def available_server(self,require_prestige=False):
        return self.session.scalar(select(GameServer).where(
            (GameServer.prestige_schema_version==1) if require_prestige else True,
            GameServer.status == RuntimeStatus.READY, GameServer.current_run_id.is_(None),
            GameServer.last_seen > func.now() - timedelta(seconds=15)
        ).order_by(GameServer.id).limit(1).with_for_update(skip_locked=True).execution_options(populate_existing=True))

    def has_available_server(self,require_prestige=False):
        # A heartbeat can briefly lock the only free process. This is not pool exhaustion.
        return self.session.scalar(select(select(GameServer.id).where(
            (GameServer.prestige_schema_version==1) if require_prestige else True,
            GameServer.status == RuntimeStatus.READY, GameServer.current_run_id.is_(None),
            GameServer.last_seen > func.now() - timedelta(seconds=15)
        ).exists()))

    def get(self, run_id, lock=False):
        return self.session.get(Run, run_id, with_for_update=lock, populate_existing=lock)

    def active(self, account_id):
        return self.session.scalar(select(Run).where(Run.account_id == account_id,
                                                     Run.status.in_([RuntimeStatus.PENDING, RuntimeStatus.READY]))
                                   .order_by(Run.created_at.desc()).limit(1))

    def create(self, **values):
        values.setdefault("ruleset_id", values["content_release_id"])
        run = Run(**values)
        self.session.add(run)
        self.session.flush()  # The server's current_run_id references this row.
        return run

    def ready_pair(self, run_id, account_id):
        return self.session.execute(select(Run, GameServer).join(GameServer, GameServer.id == Run.server_id)
                                    .where(Run.id == run_id, Run.account_id == account_id)).first()

    def checkpoint(self, run_id, lock=False):
        return self.session.get(RunCheckpoint, run_id, with_for_update=lock, populate_existing=lock)

    def save_checkpoint(self, **values):
        statement = insert(RunCheckpoint).values(**values)
        changes = {key: getattr(statement.excluded, key) for key in values if key != "run_id"}
        self.session.execute(statement.on_conflict_do_update(
            index_elements=[RunCheckpoint.run_id], set_={**changes, "created_at": func.now()}))

    def result(self, run_id):
        return self.session.get(RunResultOperation, run_id)

    def record_result(self, **values):
        self.session.add(RunResultOperation(**values))


    def start_operation(self, account_id, request_key):
        row = self.session.get(IdempotencyRecord, (account_id, "run.start", str(request_key)))
        if row is not None and row.payload_hash!=hashlib.sha256(b'DEDICATED.v1').digest():
            from ....application.errors import ApplicationError
            raise ApplicationError(409,'Start request payload or mode changed')
        return UUID(row.result_reference) if row else None

    def record_start(self, account_id, request_key, run_id):
        self.session.add(IdempotencyRecord(account_id=account_id, operation="run.start", request_key=str(request_key),
            payload_hash=hashlib.sha256(b"DEDICATED.v1").digest(), status=RewardStatus.COMPLETED,
            result_reference=str(run_id), result_json={"runId": str(run_id)}, completed_at=func.now()))


    def expired_pending(self, at, limit):
        return list(self.session.execute(select(Run.id, Run.account_id).where(
            Run.status == RuntimeStatus.PENDING, Run.started_at.is_(None),
            Run.assignment_expires_at <= at).order_by(Run.assignment_expires_at, Run.id).limit(limit)).tuples())

    def audit_result_conflict(self, run, expected, received):
        self.session.add(RunValidationAudit(run_id=run.id, ruleset_id=run.ruleset_id,
            validator_version='dedicated-match.v2', status='REVIEW', reasons=[{
                'code': 'DEDICATED_RESULT_CONFLICT', 'serverId': str(run.server_id),
                'generation': run.generation, 'expectedSha256': expected.hex(), 'receivedSha256': received.hex()}]))
