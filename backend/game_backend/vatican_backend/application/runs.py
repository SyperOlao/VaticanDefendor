import hashlib
import hmac
import json
import time
from datetime import datetime, timedelta, timezone
from uuid import uuid4

from ..grants import issue_grant
from .errors import ApplicationError


def now():
    return datetime.now(timezone.utc)


def assignment_response(run):
    return {"assigned": True, "runId": str(run.id), "accountId": str(run.account_id),
            "contentReleaseId": run.content_release_id, "profileVersion": run.profile_version,
            "displayName": run.profile_display_name, "generation": run.generation,
            "weaponsJson": json.dumps(run.weapon_snapshot)}


class RunUseCases:
    def __init__(self, uow, settings, weapons, assignment_timeout=20):
        self.uow = uow
        self.settings = settings
        self.weapons = weapons
        self.assignment_timeout = assignment_timeout

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
            server = work.runs.server(server_id, lock=True)
            if server is not None and server.current_run_id is not None:
                raise ApplicationError(409, "Server already owns a run")
            registered = work.runs.register_server(id=server_id, host=slot.host, port=slot.port,
                content_release_id=registration.content_release_id, protocol_version=registration.protocol_version,
                status="ready", last_seen=now())
            if registered is None:
                raise ApplicationError(409, "Server already owns a run")
        return {"status": "ready"}

    def server_assignment(self, server_id, credential):
        self._slot(server_id, credential)
        with self.uow() as work:
            server = work.runs.server(server_id, lock=True)
            if server is None:
                raise ApplicationError(409, "Server has not registered")
            server.last_seen = now()
            if server.current_run_id is None:
                return {"assigned": False}
            run = work.runs.get(server.current_run_id)
            if run is None or run.server_id != server_id or run.status == "failed":
                return {"assigned": False}
            return assignment_response(run)

    def acknowledge(self, server_id, credential, assignment):
        self._slot(server_id, credential)
        with self.uow() as work:
            # All run writers lock server before run; economic writers lock account first.
            server = work.runs.server(server_id, lock=True)
            run = work.runs.get(assignment.run_id, lock=True)
            if (run is None or run.server_id != server_id or run.generation != assignment.generation
                    or run.status != "pending"):
                raise ApplicationError(409, "Assignment is stale")
            if server is None or server.current_run_id != run.id or server.generation != assignment.generation:
                raise ApplicationError(409, "Server assignment changed")
            run.status = "ready"
            server.status = "occupied"
            server.last_seen = now()
        return {"status": "ready"}

    def heartbeat(self, server_id, credential, assignment):
        self._slot(server_id, credential)
        with self.uow() as work:
            server = work.runs.server(server_id, lock=True)
            if (server is None or server.current_run_id != assignment.run_id
                    or server.generation != assignment.generation or server.status != "occupied"):
                raise ApplicationError(409, "Assignment is no longer current")
            server.last_seen = now()
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
                or snapshot.get("simulationTick") != checkpoint.simulation_tick):
            raise ApplicationError(422, "Checkpoint identity mismatch")
        with self.uow() as work:
            server = work.runs.server(server_id, lock=True)
            run = work.runs.get(checkpoint.run_id, lock=True)
            if (run is None or run.server_id != server_id or run.generation != checkpoint.generation
                    or server is None or server.generation != checkpoint.generation
                    or server.current_run_id != run.id or run.status != "ready" or server.status != "occupied"
                    or snapshot.get("contentReleaseId") != run.content_release_id):
                raise ApplicationError(409, "Checkpoint owner is no longer current")
            prior = work.runs.checkpoint(run.id, lock=True)
            if prior is not None and checkpoint.simulation_tick < prior.simulation_tick:
                raise ApplicationError(409, "Checkpoint tick moved backwards")
            if (prior is not None and checkpoint.simulation_tick == prior.simulation_tick
                    and hmac.compare_digest(prior.sha256, digest)):
                return {"accepted": True, "simulationTick": checkpoint.simulation_tick, "replayed": True}
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
            if server.last_seen > now() - timedelta(seconds=15):
                return {"assigned": False, "recoveryPending": True}
            run = work.runs.get(server.current_run_id, lock=True)
            checkpoint = work.runs.checkpoint(server.current_run_id)
            if (run is None or run.server_id != server_id or run.status not in ("ready", "pending")
                    or checkpoint is None or checkpoint.generation > run.generation or server.generation != run.generation):
                raise ApplicationError(409, "No recoverable checkpoint for assigned run")
            if (run.content_release_id != registration.content_release_id
                    or server.protocol_version != registration.protocol_version
                    or run.content_release_id != server.content_release_id
                    or server.host != slot.host or server.port != slot.port):
                raise ApplicationError(409, "Recovery content or server slot changed")
            if (checkpoint.schema_version != 4
                    or hashlib.sha256(checkpoint.snapshot_json.encode("utf-8")).digest() != checkpoint.sha256):
                raise ApplicationError(409, "Stored checkpoint is corrupt or unsupported")
            run.generation += 1
            run.status = "pending"
            server.generation = run.generation
            server.status = "assigned"
            server.last_seen = now()
            return {**assignment_response(run), "snapshotJson": checkpoint.snapshot_json}

    def abandon(self, server_id, credential, assignment):
        self._slot(server_id, credential)
        with self.uow() as work:
            server = work.runs.server(server_id, lock=True)
            run = work.runs.get(assignment.run_id, lock=True)
            if run is None or run.server_id != server_id or run.generation != assignment.generation:
                raise ApplicationError(409, "Run is not active")
            if run.status == "failed":
                return {"status": "abandoned", "replayed": True}
            if run.status != "ready":
                raise ApplicationError(409, "Run is not active")
            if (server is None or server.current_run_id != run.id or server.generation != assignment.generation
                    or server.status != "occupied"):
                raise ApplicationError(409, "Run assignment is no longer current")
            server.status = "unavailable"
            server.current_run_id = None
            run.status = "failed"
        return {"status": "abandoned"}

    def apply_result(self, server_id, credential, result):
        self._slot(server_id, credential)
        payload = {"runId": str(result.run_id), "accountId": str(result.account_id),
                   "contentReleaseId": result.content_release_id, "generation": result.generation,
                   "outcome": result.outcome, "churchLevel": result.church_level}
        digest = hashlib.sha256(json.dumps(payload, sort_keys=True, separators=(",", ":")).encode("utf-8")).digest()
        with self.uow() as work:
            work.profiles.get(result.account_id, lock=True)
            server = work.runs.server(server_id, lock=True)
            run = work.runs.get(result.run_id, lock=True)
            if (run is None or run.server_id != server_id or run.account_id != result.account_id
                    or run.content_release_id != result.content_release_id or run.generation != result.generation):
                raise ApplicationError(409, "Result assignment mismatch")
            prior = work.runs.result(run.id)
            if prior is not None:
                if not hmac.compare_digest(prior.payload_hash, digest):
                    raise ApplicationError(409, "Conflicting result for this RunId")
                return {"runId": str(run.id), "outcome": prior.outcome, "permanentReward": prior.permanent_reward,
                        "profileVersion": prior.profile_version, "replayed": True}
            if (run.status != "ready" or server is None or server.current_run_id != run.id
                    or server.generation != result.generation):
                raise ApplicationError(409, "Run assignment is no longer current")
            if run.reward_per_church_level is None:
                raise ApplicationError(422, "No pinned reward policy for content release")
            self.weapons.grant_match_weapon(work, result.account_id, run)
            reward = run.reward_per_church_level * result.church_level
            profile = work.profiles.change_currency(result.account_id, reward)
            work.runs.record_result(run_id=run.id, account_id=result.account_id, server_id=server_id,
                generation=result.generation, content_release_id=result.content_release_id, payload_hash=digest,
                outcome=result.outcome, permanent_reward=reward, profile_version=profile.version, church_level=result.church_level)
            run.status = "completed"
            run.outcome = result.outcome
            run.permanent_reward = reward
            run.completed_at = now()
            server.current_run_id = None
            server.status = "unavailable"
            server.last_seen = now()
            return {"runId": str(run.id), "outcome": result.outcome, "permanentReward": reward,
                    "profileVersion": profile.version, "replayed": False}

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
        pair = work.runs.ready_pair(run_id, account_id)
        if pair is None:
            raise ApplicationError(404, "Run not found")
        run, server = pair
        if (run.status != "ready" or server.current_run_id != run.id or server.generation != run.generation
                or server.last_seen < now() - timedelta(seconds=15)):
            raise ApplicationError(503, "Run server is not ready")
        return self._grant(run, server)

    def renew_grant(self, run_id, account_id):
        with self.uow() as work:
            return self._ready(work, run_id, account_id)

    def assignment_status(self, run_id, account_id):
        with self.uow() as work:
            run = work.runs.get(run_id)
            if run is None or run.account_id != account_id:
                raise ApplicationError(404, "Run not found")
            if run.status == "pending":
                return {"runId": str(run_id), "status": "pending"}
            return self._ready(work, run_id, account_id)

    def _reserve_once(self, account_id):
        run_id = None
        busy = False
        with self.uow() as work:
            work.profiles.get(account_id, lock=True)
            existing = work.runs.active(account_id)
            if existing is not None and existing.simulation_mode == "LOCAL":
                raise ApplicationError(409, "Finish the active local run before requesting a dedicated process")
            if existing is not None:
                server = work.runs.server(existing.server_id, lock=True)
                existing = work.runs.get(existing.id, lock=True)
                stale = (server is None or server.current_run_id != existing.id
                    or (existing.status == "ready" and server.last_seen <= now() - timedelta(seconds=15))
                    or (existing.status == "pending" and existing.created_at <= now() - timedelta(seconds=30)))
                if stale:
                    if work.runs.checkpoint(existing.id) is not None and server is not None:
                        raise ApplicationError(503, "Run is awaiting checkpoint recovery")
                    existing.status = "failed"
                    if server is not None and server.current_run_id == existing.id:
                        server.status = "unavailable"
                        server.current_run_id = None
                    existing = None
            if existing is not None:
                run_id = existing.id
            else:
                server = work.runs.available_server()
                if server is None:
                    busy = work.runs.has_available_server()
                if server is not None:
                    profile = work.profiles.get(account_id)
                    policy = self.settings.reward_policies.get(server.content_release_id)
                    run = work.runs.create(id=uuid4(), account_id=account_id, server_id=server.id,
                        content_release_id=server.content_release_id, profile_version=profile.version,
                        profile_display_name=profile.display_name, generation=server.generation + 1,
                        status="pending", reward_per_church_level=policy.per_church_level if policy else None)
                    self.weapons.pin_loadout(work, account_id, run)
                    server.status = "assigned"
                    server.current_run_id = run.id
                    server.generation = run.generation
                    run_id = run.id
        # Stale cleanup commits even when there is no spare server.
        return run_id, busy

    def _reserve(self, account_id):
        for attempt in range(5):
            run_id, busy = self._reserve_once(account_id)
            if run_id is not None:
                return run_id
            if not busy:
                break
            # No transaction or account lock is held while waiting for a short heartbeat.
            time.sleep(0.02 * (attempt + 1))
        raise ApplicationError(503, "No live game process is available")

    def _expire_assignment(self, run_id, account_id):
        with self.uow() as work:
            original = work.runs.get(run_id)
            if original is None:
                return None
            server = work.runs.server(original.server_id, lock=True)
            run = work.runs.get(run_id, lock=True)
            # An ACK that wins the lock at the deadline must keep its occupied process.
            if run.status == "ready":
                return self._ready(work, run_id, account_id)
            if run.status == "pending":
                run.status = "failed"
                if server is not None and server.current_run_id == run_id and server.status == "assigned":
                    server.current_run_id = None
                    server.status = "ready"
        return None

    def assign(self, account_id, respond_async=False):
        run_id = self._reserve(account_id)
        if respond_async:
            return {"runId": str(run_id), "status": "pending"}
        deadline = time.monotonic() + self.assignment_timeout
        while time.monotonic() < deadline:
            with self.uow() as work:
                run = work.runs.get(run_id)
                if run is not None and run.status == "ready":
                    return self._ready(work, run_id, account_id)
                if run is None or run.status == "failed":
                    break
            time.sleep(0.2)
        confirmed = self._expire_assignment(run_id, account_id)
        if confirmed is not None:
            return confirmed
        raise ApplicationError(503, "Game process did not confirm the assignment")
