from .catalog import GameErrorCode, Outcome, RewardStatus, RuntimeStatus, SimulationMode
import hashlib
import hmac
import json
import time
from datetime import timedelta
from uuid import uuid4

from ..domain.grants import issue_grant
from .ports import UnitOfWorkFactory, InventoryGateway, GamePolicy
from .errors import ApplicationError
from .energy import EnergyService
from .progression import ProgressionService
from ..domain.progression import RunStats
from ..domain.match_results import validate_snapshot
from dataclasses import replace
from ..domain.clock import Clock, UtcClock
from .settlements import delivery_response, settle


def assignment_response(run):
    return {"assigned": True, "runId": str(run.id), "accountId": str(run.account_id),
            "contentReleaseId": run.content_release_id, "rulesetId": run.ruleset_id, "profileVersion": run.profile_version,
            "displayName": run.profile_display_name, "generation": run.generation,
            "weaponsJson": json.dumps(run.weapon_snapshot),
            "prestigeJson": json.dumps(run.prestige_snapshot or {"schemaVersion":1,"upgrades":[],"modifiers":[]}),
            "assignmentExpiresAt": run.assignment_expires_at.isoformat() if run.assignment_expires_at else None}




class RunUseCases:
    def __init__(self, uow: UnitOfWorkFactory, settings: GamePolicy, inventory: InventoryGateway, assignment_timeout=20, clock: Clock | None = None):
        self.uow = uow
        self.settings = settings
        self.inventory = inventory
        self.assignment_timeout = assignment_timeout
        self.clock = clock or UtcClock()
        self.energy = EnergyService(settings.energy_ruleset_id)
        self.progression = ProgressionService()

    def _slot(self, server_id, credential):
        slot = self.settings.servers.get(server_id)
        if slot is None or credential is None or not hmac.compare_digest(slot.secret, credential):
            raise ApplicationError(401, "Server credential required")
        return slot

    @staticmethod
    def _transport(slot, registration):
        if registration.transport != slot.transport:
            raise ApplicationError(409, "Game transport does not match configured slot")

    def register_server(self, server_id, credential, registration):
        slot = self._slot(server_id, credential)
        self._transport(slot, registration)
        if registration.content_release_id not in self.settings.reward_policies:
            raise ApplicationError(422, "No approved reward policy for content release")
        with self.uow() as work:
            work.runs.ensure_ruleset(registration.content_release_id)
            ruleset_id = registration.ruleset_id or registration.content_release_id
            if registration.ruleset_id is not None:
                self.progression.pin(work, ruleset_id, registration.content_release_id)
            server = work.runs.server(server_id, lock=True)
            if server is not None and server.current_run_id is not None:
                raise ApplicationError(409, "Server already owns a run")
            registered = work.runs.register_server(id=server_id, host=slot.host, port=slot.port,
                content_release_id=registration.content_release_id, protocol_version=registration.protocol_version, ruleset_id=ruleset_id,
                status=RuntimeStatus.READY, last_seen=self.clock.now(),prestige_schema_version=registration.prestige_schema_version)
            if registered is None:
                raise ApplicationError(409, "Server already owns a run")
        return {"status": RuntimeStatus.READY}

    def server_assignment(self, server_id, credential):
        self._slot(server_id, credential)
        with self.uow() as work:
            server = work.runs.server(server_id, lock=True)
            if server is None:
                raise ApplicationError(409, "Server has not registered")
            server.last_seen = self.clock.now()
            if server.current_run_id is None:
                return {"assigned": False}
            run = work.runs.get(server.current_run_id)
            if run is None or run.server_id != server_id or run.status == RuntimeStatus.FAILED:
                return {"assigned": False}
            return assignment_response(run)

    def acknowledge(self, server_id, credential, assignment):
        self._slot(server_id, credential)
        with self.uow() as work:
            # All run writers lock server before run; economic writers lock account first.
            server = work.runs.server(server_id, lock=True)
            run = work.runs.get(assignment.run_id, lock=True)
            if (run is not None and server is not None and run.server_id == server_id
                    and run.generation == assignment.generation and run.status == RuntimeStatus.READY
                    and server.current_run_id == run.id and server.generation == assignment.generation):
                return {"status": RuntimeStatus.READY, "replayed": True}
            if (run is None or run.server_id != server_id or run.generation != assignment.generation
                    or run.status != RuntimeStatus.PENDING):
                raise ApplicationError(409, "Assignment is stale")
            if server is None or server.current_run_id != run.id or server.generation != assignment.generation:
                raise ApplicationError(409, "Server assignment changed")
            if run.assignment_expires_at is not None and run.assignment_expires_at <= self.clock.now():
                raise ApplicationError(409, "Assignment confirmation deadline expired", code=GameErrorCode.ASSIGNMENT_EXPIRED)
            run.started_at = run.started_at or self.clock.now()
            run.status = RuntimeStatus.READY
            server.status = RuntimeStatus.OCCUPIED
            server.last_seen = self.clock.now()
        return {"status": RuntimeStatus.READY}

    def heartbeat(self, server_id, credential, assignment):
        self._slot(server_id, credential)
        with self.uow() as work:
            server = work.runs.server(server_id, lock=True)
            if (server is None or server.current_run_id != assignment.run_id
                    or server.generation != assignment.generation or server.status != RuntimeStatus.OCCUPIED):
                raise ApplicationError(409, "Assignment is no longer current")
            server.last_seen = self.clock.now()
        return {"current": True}

    def publish_checkpoint(self, server_id, credential, checkpoint):
        self._slot(server_id, credential)
        digest = hashlib.sha256(checkpoint.snapshot_json.encode("utf-8")).digest()
        if not hmac.compare_digest(digest.hex(), checkpoint.sha256):
            raise ApplicationError(422, "Checkpoint checksum mismatch")
        try:
            snapshot = json.loads(checkpoint.snapshot_json)
        except (ValueError, TypeError):
            raise ApplicationError(422, "Checkpoint JSON is invalid") from None
        if (not isinstance(snapshot, dict) or snapshot.get("schemaVersion") != checkpoint.schema_version
                or snapshot.get("runId") != str(checkpoint.run_id)
                or snapshot.get("simulationTick") != checkpoint.simulation_tick
                or checkpoint.schema_version == 6 and (type(snapshot.get("checkpointSequence")) is not int or snapshot["checkpointSequence"] < 1)):
            raise ApplicationError(422, "Checkpoint identity mismatch")
        with self.uow() as work:
            server = work.runs.server(server_id, lock=True)
            run = work.runs.get(checkpoint.run_id, lock=True)
            if (run is None or run.server_id != server_id or run.generation != checkpoint.generation
                    or server is None or server.generation != checkpoint.generation
                    or server.current_run_id != run.id or run.status != RuntimeStatus.READY or server.status != RuntimeStatus.OCCUPIED
                    or snapshot.get("contentReleaseId") != run.content_release_id):
                raise ApplicationError(409, "Checkpoint owner is no longer current")
            prior = work.runs.checkpoint(run.id, lock=True)
            if prior is not None and checkpoint.simulation_tick < prior.simulation_tick:
                raise ApplicationError(409, "Checkpoint tick moved backwards")
            if (prior is not None and checkpoint.simulation_tick == prior.simulation_tick
                    and hmac.compare_digest(prior.sha256, digest)):
                return {"accepted": True, "simulationTick": checkpoint.simulation_tick, "replayed": True}
            if prior is not None and checkpoint.simulation_tick == prior.simulation_tick:
                previous = json.loads(prior.snapshot_json)
                if (checkpoint.schema_version != 6 or snapshot["checkpointSequence"] <= previous.get("checkpointSequence",0)):
                    raise ApplicationError(409, "Conflicting checkpoint at this tick")
            work.runs.save_checkpoint(run_id=run.id, generation=checkpoint.generation,
                schema_version=checkpoint.schema_version, simulation_tick=checkpoint.simulation_tick,
                snapshot_json=checkpoint.snapshot_json, sha256=digest)
        return {"accepted": True, "simulationTick": checkpoint.simulation_tick}

    def recover(self, server_id, credential, registration):
        slot = self._slot(server_id, credential)
        self._transport(slot, registration)
        with self.uow() as work:
            server = work.runs.server(server_id, lock=True)
            if server is None or server.current_run_id is None:
                return {"assigned": False}
            if server.last_seen > self.clock.now() - timedelta(seconds=15):
                return {"assigned": False, "recoveryPending": True}
            run = work.runs.get(server.current_run_id, lock=True)
            checkpoint = work.runs.checkpoint(server.current_run_id)
            if (run is None or run.server_id != server_id or run.status not in (RuntimeStatus.READY, RuntimeStatus.PENDING)
                    or checkpoint is None or checkpoint.generation > run.generation or server.generation != run.generation):
                raise ApplicationError(409, "No recoverable checkpoint for assigned run")
            if (run.ruleset_id != (registration.ruleset_id or registration.content_release_id)
                    or run.ruleset_id != (server.ruleset_id or server.content_release_id)
                    or run.content_release_id != registration.content_release_id
                    or server.protocol_version != registration.protocol_version
                    or run.content_release_id != server.content_release_id
                    or server.host != slot.host or server.port != slot.port):
                raise ApplicationError(409, "Recovery content or server slot changed")
            if run.prestige_snapshot and run.prestige_snapshot['modifiers'] and registration.prestige_schema_version!=1:
                raise ApplicationError(409,"Recovery server cannot apply prestige bonuses")
            if (checkpoint.schema_version not in (4, 5, 6)
                    or hashlib.sha256(checkpoint.snapshot_json.encode("utf-8")).digest() != checkpoint.sha256):
                raise ApplicationError(409, "Stored checkpoint is corrupt or unsupported")
            checkpoint_age = max(0,(self.clock.now()-checkpoint.created_at).total_seconds())
            disconnected_age = max(0,(self.clock.now()-server.last_seen).total_seconds())
            if checkpoint.generation < run.generation:
                # A previous recovery died before publishing: do not renew its retention budget.
                disconnected_age = max(disconnected_age,checkpoint_age)
            run.assignment_expires_at = self.clock.now() + timedelta(seconds=self.settings.assignment_lease_seconds)
            run.generation += 1
            run.status = RuntimeStatus.PENDING
            server.generation = run.generation
            server.status = RuntimeStatus.ASSIGNED
            server.last_seen = self.clock.now()
            return {**assignment_response(run), "snapshotJson": checkpoint.snapshot_json,
                    "checkpointAgeSeconds":checkpoint_age,"recoveryDisconnectedSeconds":disconnected_age}

    def abandon(self, server_id, credential, assignment):
        self._slot(server_id, credential)
        with self.uow() as work:
            server = work.runs.server(server_id, lock=True)
            run = work.runs.get(assignment.run_id, lock=True)
            if run is None or run.server_id != server_id or run.generation != assignment.generation:
                raise ApplicationError(409, "Run is not active")
            if run.status == RuntimeStatus.FAILED:
                return {"status": "abandoned", "replayed": True}
            if run.status != RuntimeStatus.READY:
                raise ApplicationError(409, "Run is not active")
            if (server is None or server.current_run_id != run.id or server.generation != assignment.generation
                    or server.status != RuntimeStatus.OCCUPIED):
                raise ApplicationError(409, "Run assignment is no longer current")
            server.status = RuntimeStatus.UNAVAILABLE
            server.current_run_id = None
            run.status = RuntimeStatus.FAILED
        return {"status": "abandoned"}

    def apply_result(self, server_id, credential, result):
        self._slot(server_id, credential)
        if (result.outcome not in (Outcome.VICTORY, Outcome.DEFEAT)
                or type(result.church_level) is not int or not 1 <= result.church_level <= 5
                or type(result.generation) is not int or result.generation < 1):
            raise ApplicationError(422, "Invalid dedicated run result")
        if result.schema_version not in (1, 2):
            raise ApplicationError(422, "Unsupported result version")
        statistics = None
        if result.schema_version == 2:
            if not result.ruleset_id:
                raise ApplicationError(409, "Result server identity mismatch")
            try:
                if any(result.stats.snapshot().values()):
                    raise ValueError('v2 XP counters must be derived from match statistics')
                statistics, stats = validate_snapshot(result.statistics)
                result = replace(result, stats=stats)
            except (ValueError, TypeError, KeyError, AttributeError):
                raise ApplicationError(422, "Invalid MatchResultSnapshot") from None
        elif result.statistics is not None or result.server_id is not None or result.ruleset_id is not None:
            raise ApplicationError(422, "Versioned match fields require result v2")
        try:
            if not isinstance(result.stats, RunStats):
                raise ValueError()
            stats_payload = result.stats.snapshot()
        except (ValueError, TypeError):
            raise ApplicationError(422, "Invalid dedicated XP counters") from None
        payload = {"runId": str(result.run_id), "accountId": str(result.account_id),
                   "contentReleaseId": result.content_release_id, "generation": result.generation,
                   "outcome": result.outcome, "churchLevel": result.church_level, "stats": stats_payload}
        if statistics is not None:
            payload.update(schemaVersion=2, serverId=str(result.server_id), rulesetId=result.ruleset_id, statistics=statistics)
        digest = hashlib.sha256(json.dumps(payload, sort_keys=True, separators=(",", ":")).encode("utf-8")).digest()
        conflict = False
        with self.uow() as work:
            candidate = work.runs.get(result.run_id)
            if candidate is None or candidate.server_id != server_id or candidate.simulation_mode != SimulationMode.DEDICATED:
                raise ApplicationError(409, "Result assignment mismatch")
            # Lock the actual owner, never an account chosen by the submitted body.
            if work.profiles.get(candidate.account_id, lock=True) is None:
                raise ApplicationError(404, "Profile not found")
            server = work.runs.server(server_id, lock=True)
            run = work.runs.get(result.run_id, lock=True)
            prior = work.runs.result(run.id)
            mismatch = (run.server_id != server_id or run.account_id != result.account_id
                or run.content_release_id != result.content_release_id or run.generation != result.generation
                or statistics is not None and (run.ruleset_id != result.ruleset_id or result.server_id != server_id))
            if mismatch:
                if prior is None:
                    raise ApplicationError(409, "Result assignment mismatch")
                work.runs.audit_result_conflict(run, prior.payload_hash, digest)
                conflict = True
            if prior is not None and not mismatch:
                matches = hmac.compare_digest(prior.payload_hash, digest)
                if (not matches and run.xp_rules_snapshot is None and prior.progression is None
                        and not any(stats_payload.values())):
                    # Existing receipts used the older result hash without XP counters.
                    legacy = {key: value for key, value in payload.items() if key != "stats"}
                    legacy_digest = hashlib.sha256(json.dumps(legacy, sort_keys=True, separators=(",", ":")).encode()).digest()
                    matches = hmac.compare_digest(prior.payload_hash, legacy_digest)
                if not matches:
                    work.runs.audit_result_conflict(run, prior.payload_hash, digest)
                    conflict = True
                else:
                    return {"runId": str(run.id), "outcome": prior.outcome, "permanentReward": prior.permanent_reward,
                            "profileVersion": prior.profile_version, "rewardStatus": work.messages.reward_status(run.id),
                            "progression": prior.progression, **delivery_response(work,run.id), "replayed": True}
            if not conflict:
                if (run.status != RuntimeStatus.READY or server is None or server.current_run_id != run.id
                        or server.generation != result.generation or server.status != RuntimeStatus.OCCUPIED):
                    raise ApplicationError(409, "Run assignment is no longer current")
                receipt=settle(work,run,result,payload,digest,self.clock.now(),
                    rating_board=getattr(self.settings,'rating_board_code','run-xp'),analytics=True)
                server.current_run_id = None
                server.status = RuntimeStatus.UNAVAILABLE
                server.last_seen = self.clock.now()
                return {**receipt,'rewardStatus':work.messages.reward_status(run.id),
                        **delivery_response(work,run.id),'replayed':False}
        # Commit the conflict audit before raising; a transaction rollback must not erase it.
        raise ApplicationError(409, "Conflicting result for this RunId")

    def _grant(self, run, server):
        slot = self.settings.servers.get(run.server_id)
        if slot is None:
            raise ApplicationError(503, "Assigned server is no longer configured")
        token, expires_at = issue_grant(run.account_id, run.id, slot, run.generation, self.settings.grant_ttl_seconds)
        return {"runId": str(run.id), "serverId": str(run.server_id), "host": slot.host, "port": slot.port,
                "transport": slot.transport, "certificateName": slot.certificate_name,
                "contentReleaseId": run.content_release_id, "protocolVersion": server.protocol_version,
                "profileVersion": run.profile_version, "profileDisplayName": run.profile_display_name,
                "grant": token, "grantExpiresAt": expires_at}

    def _ready(self, work, run_id, account_id):
        local=work.runs.get(run_id)
        if local is not None and local.account_id==account_id and local.simulation_mode==SimulationMode.LOCAL:
            raise ApplicationError(409,'LOCAL runs do not issue dedicated server grants')
        if local is not None and local.account_id==account_id and local.status==RuntimeStatus.FAILED:
            raise ApplicationError(410,"SessionExpired",code=GameErrorCode.SESSION_EXPIRED)
        pair = work.runs.ready_pair(run_id, account_id)
        if pair is None:
            raise ApplicationError(404, "Run not found")
        run, server = pair
        if (run.status != RuntimeStatus.READY or server.current_run_id != run.id or server.generation != run.generation
                or server.last_seen < self.clock.now() - timedelta(seconds=15)):
            raise ApplicationError(503, "Run server is not ready")
        return self._grant(run, server)

    def renew_grant(self, run_id, account_id):
        with self.uow() as work:
            return self._ready(work, run_id, account_id)

    def assignment_status(self, run_id, account_id):
        self._expire_assignment(run_id, account_id, only_if_due=True)
        with self.uow() as work:
            run = work.runs.get(run_id)
            if run is None or run.account_id != account_id:
                raise ApplicationError(404, "Run not found")
            if run.simulation_mode==SimulationMode.LOCAL:
                from .local_runs import local_response
                return local_response(work,run)
            if run.status == RuntimeStatus.PENDING:
                return {"runId": str(run_id), "status": RuntimeStatus.PENDING,
                        "assignmentExpiresAt": run.assignment_expires_at.isoformat() if run.assignment_expires_at else None}
            if run.status == RuntimeStatus.COMPLETED:
                result = work.runs.result(run.id)
                return {"runId": str(run.id), "status": RuntimeStatus.COMPLETED, "outcome": run.outcome,
                        "permanentReward": run.permanent_reward, "profileVersion": result.profile_version if result else run.profile_version,
                        "rewardStatus": work.messages.reward_status(run.id),
                        "progression": result.progression if result else None, **delivery_response(work,run.id)}
            if run.status == RuntimeStatus.FAILED:
                return {"runId": str(run.id), "status": RuntimeStatus.FAILED}
            return self._ready(work, run_id, account_id)

    def _reserve_once(self, account_id, weapon_snapshot, request_key=None):
        run_id = None
        busy = False
        with self.uow() as work:
            if work.profiles.get(account_id, lock=True) is None:
                raise ApplicationError(404, "Profile not found")
            prior = work.runs.start_operation(account_id, request_key) if request_key else None
            if prior is not None:
                return prior, False
            existing = work.runs.active(account_id)
            if existing is not None and existing.simulation_mode == SimulationMode.LOCAL:
                from .local_runs import LocalRunUseCases
                LocalRunUseCases._expire(work,existing,self.clock.now())
                if existing.status==RuntimeStatus.FAILED:existing=None
                else:raise ApplicationError(409, "Finish the active local run before requesting a dedicated process")
            if existing is not None:
                server = work.runs.server(existing.server_id, lock=True)
                existing = work.runs.get(existing.id, lock=True)
                stale = (server is None or server.current_run_id != existing.id
                    or (existing.status == RuntimeStatus.READY and server.last_seen <= self.clock.now() - timedelta(seconds=15))
                    or (existing.status == RuntimeStatus.PENDING and (
                        existing.assignment_expires_at <= self.clock.now() if existing.assignment_expires_at is not None
                        else existing.created_at <= self.clock.now() - timedelta(seconds=30))))
                if stale:
                    if work.runs.checkpoint(existing.id) is not None and server is not None:
                        raise ApplicationError(503, "Run is awaiting checkpoint recovery")
                    if existing.started_at is None:
                        self.energy.refund_unstarted(work, existing, self.clock.now())
                    existing.status = RuntimeStatus.FAILED
                    if server is not None and server.current_run_id == existing.id:
                        server.status = RuntimeStatus.UNAVAILABLE
                        server.current_run_id = None
                    existing = None
            if existing is not None:
                run_id = existing.id
            else:
                prestige=work.prestige.snapshot(account_id)
                server = work.runs.available_server(require_prestige=True) if prestige['modifiers'] else work.runs.available_server()
                if server is None:
                    busy = work.runs.has_available_server(require_prestige=True) if prestige['modifiers'] else work.runs.has_available_server()
                if server is not None:
                    # The reservation, energy charge and request record commit together.
                    profile = work.profiles.get(account_id)
                    policy = self.settings.reward_policies.get(server.content_release_id)
                    ruleset_id = server.ruleset_id or server.content_release_id
                    xp_rules = self.progression.pin(work, ruleset_id, server.content_release_id)
                    run = work.runs.create(id=uuid4(), account_id=account_id, server_id=server.id,
                        content_release_id=server.content_release_id, ruleset_id=ruleset_id, profile_version=profile.version,
                        profile_display_name=profile.display_name, generation=server.generation + 1,
                        status=RuntimeStatus.PENDING, reward_per_church_level=policy.per_church_level if policy else None,
                        assignment_expires_at=self.clock.now() + timedelta(seconds=self.settings.assignment_lease_seconds))
                    self.energy.spend(work, account_id, run.id, self.clock.now())
                    run.prestige_snapshot = prestige
                    run.weapon_snapshot = weapon_snapshot
                    run.xp_rules_snapshot = xp_rules
                    server.status = RuntimeStatus.ASSIGNED
                    server.current_run_id = run.id
                    server.generation = run.generation
                    run_id = run.id
            if run_id is not None and request_key is not None:
                work.runs.record_start(account_id, request_key, run_id)
        # Stale cleanup commits even when there is no spare server.
        return run_id, busy

    def _reserve(self, account_id, request_key=None):
        if request_key is not None:
            with self.uow() as work:
                prior = work.runs.start_operation(account_id, request_key)
                if prior is not None:
                    return prior
        # The remote call finishes before acquiring a database connection or a lock.
        weapon_snapshot = self.inventory.snapshot(account_id)
        for attempt in range(5):
            run_id, busy = self._reserve_once(account_id, weapon_snapshot, request_key)
            if run_id is not None:
                return run_id
            if not busy:
                break
            # No transaction or account lock is held while waiting for a short heartbeat.
            time.sleep(0.02 * (attempt + 1))
        raise ApplicationError(503, "No live game process is available")

    def _expire_assignment(self, run_id, account_id, only_if_due=False):
        with self.uow() as work:
            # Every energy writer locks profile before server/run, including the timeout worker.
            if work.profiles.get(account_id, lock=True) is None:
                return None
            original = work.runs.get(run_id)
            if original is None or original.account_id != account_id:
                return None
            if only_if_due and (original.status != RuntimeStatus.PENDING or original.started_at is not None
                    or original.assignment_expires_at is None or original.assignment_expires_at > self.clock.now()):
                return None
            server = work.runs.server(original.server_id, lock=True)
            run = work.runs.get(run_id, lock=True)
            # An ACK which won the locks keeps its process and its charge.
            if run.status == RuntimeStatus.READY:
                return None if only_if_due else self._ready(work, run_id, account_id)
            if run.status != RuntimeStatus.PENDING or run.started_at is not None:
                return None
            if only_if_due and run.assignment_expires_at is not None and run.assignment_expires_at > self.clock.now():
                return None
            self.energy.refund_unstarted(work, run, self.clock.now())
            run.status = RuntimeStatus.FAILED
            if server is not None and server.current_run_id == run_id and server.status == RuntimeStatus.ASSIGNED:
                server.current_run_id = None
                server.status = (RuntimeStatus.READY if server.last_seen > self.clock.now() - timedelta(seconds=15)
                                 else RuntimeStatus.UNAVAILABLE)
        return None

    def expire_pending(self, limit=20):
        if type(limit) is not int or not 1 <= limit <= 100:
            raise ApplicationError(422, "Invalid assignment cleanup limit")
        with self.uow() as work:
            expired = work.runs.expired_pending(self.clock.now(), limit)
        for run_id, account_id in expired:
            self._expire_assignment(run_id, account_id, only_if_due=True)
        return len(expired)

    def assign(self, account_id, respond_async=False, request_key=None):
        run_id = self._reserve(account_id, request_key)
        if respond_async:
            return self.assignment_status(run_id, account_id)
        deadline = time.monotonic() + self.assignment_timeout
        while time.monotonic() < deadline:
            with self.uow() as work:
                run = work.runs.get(run_id)
                if run is not None and run.status == RuntimeStatus.READY:
                    return self._ready(work, run_id, account_id)
                if run is None or run.status == RuntimeStatus.FAILED:
                    break
            time.sleep(0.2)
        confirmed = self._expire_assignment(run_id, account_id)
        if confirmed is not None:
            return confirmed
        raise ApplicationError(503, "Game process did not confirm the assignment")
