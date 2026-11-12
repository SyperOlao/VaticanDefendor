import hashlib
import hmac
import json
import secrets
import time
from contextlib import asynccontextmanager
from datetime import datetime, timedelta, timezone
from uuid import UUID, uuid4

import psycopg
from fastapi import Depends, FastAPI, Header, HTTPException, Path, status
from pydantic import BaseModel, ConfigDict, Field
from psycopg.rows import dict_row
from psycopg_pool import ConnectionPool, PoolTimeout, TooManyRequests
from fastapi.responses import JSONResponse

from .grants import issue_grant
from .settings import ServerSlot, Settings


class StrictBody(BaseModel):
    model_config = ConfigDict(extra="forbid")


class ProfilePatch(StrictBody):
    expectedVersion: int = Field(ge=1)
    displayName: str = Field(min_length=1, max_length=32)


class ServerReady(StrictBody):
    contentReleaseId: str = Field(min_length=1, max_length=128)
    protocolVersion: int = Field(ge=1)
    transport: str = "udp-loopback"


class AssignmentAck(StrictBody):
    runId: UUID
    generation: int = Field(ge=1)


class CheckpointPublish(StrictBody):
    runId: UUID
    generation: int = Field(ge=1)
    schemaVersion: int = Field(ge=4, le=4)
    simulationTick: int = Field(ge=0)
    snapshotJson: str = Field(min_length=2, max_length=8_000_000)
    sha256: str = Field(pattern="^[0-9a-f]{64}$")


class RunResult(StrictBody):
    runId: UUID
    accountId: UUID
    contentReleaseId: str = Field(min_length=1, max_length=128)
    generation: int = Field(ge=1)
    outcome: str = Field(pattern="^(victory|defeat)$")
    churchLevel: int = Field(ge=1, le=1000)


def create_app(settings: Settings | None = None) -> FastAPI:
    settings = settings or Settings.from_environment()
    pool = ConnectionPool(settings.database_url, min_size=1, max_size=settings.pool_size,
                          timeout=3, max_waiting=100, open=False,
                          kwargs={"row_factory": dict_row, "connect_timeout": 3})

    @asynccontextmanager
    async def lifespan(app):
        pool.open()
        try:
            yield
        finally:
            pool.close()

    app = FastAPI(title="VaticanDefendor Backend", version="1", lifespan=lifespan)

    from .weapons import LoadCatalog, PinLoadout, GrantMatchWeapon
    weapon_catalog = LoadCatalog()

    @app.exception_handler(PoolTimeout)
    @app.exception_handler(TooManyRequests)
    async def pool_unavailable(request, error):
        return JSONResponse(status_code=503, content={"detail": "Database capacity unavailable"})

    def connect():
        return pool.connection()

    def account_from_bearer(authorization: str | None = Header(default=None)) -> UUID:
        if authorization is None or not authorization.startswith("Bearer "):
            raise HTTPException(status.HTTP_401_UNAUTHORIZED, "Bearer credential required")
        token = authorization[7:]
        if len(token) != 43:
            raise HTTPException(status.HTTP_401_UNAUTHORIZED, "Invalid guest credential")
        digest = hashlib.sha256(token.encode("ascii", errors="ignore")).digest()
        with connect() as db:
            row = db.execute(
                "SELECT account_id FROM guest_credentials WHERE token_hash=%s AND expires_at>now()",
                (digest,),
            ).fetchone()
            if row is None:
                row = db.execute(
                    "SELECT account_id FROM auth_sessions WHERE access_hash=%s "
                    "AND access_expires_at>now() AND revoked_at IS NULL", (digest,)
                ).fetchone()
        if row is None:
            raise HTTPException(status.HTTP_401_UNAUTHORIZED, "Invalid or expired guest credential")
        return row["account_id"]

    def server_slot(server_id: UUID, x_server_secret: str | None) -> ServerSlot:
        slot = settings.servers.get(server_id)
        if slot is None or x_server_secret is None or not hmac.compare_digest(slot.secret, x_server_secret):
            raise HTTPException(status.HTTP_401_UNAUTHORIZED, "Server credential required")
        return slot

    def grant_response(run: dict) -> dict:
        slot = settings.servers.get(run["server_id"])
        if slot is None:
            raise HTTPException(status.HTTP_503_SERVICE_UNAVAILABLE, "Assigned server is no longer configured")
        token, expires_at = issue_grant(run["account_id"], run["id"], slot,
                                        run["generation"], settings.grant_ttl_seconds)
        return {
            "runId": str(run["id"]),
            "serverId": str(run["server_id"]),
            "host": slot.host,
            "port": slot.port,
            "transport": slot.transport,
            "certificateName": slot.certificate_name,
            "contentReleaseId": run["content_release_id"],
            "protocolVersion": run["protocol_version"],
            "profileVersion": run["profile_version"],
            "profileDisplayName": run["profile_display_name"],
            "grant": token,
            "grantExpiresAt": expires_at,
        }

    def ready_run(db, run_id: UUID, account_id: UUID) -> dict:
        row = db.execute(
            """SELECT r.*, s.current_run_id, s.generation AS server_generation,
                      s.last_seen, s.protocol_version
               FROM runs r JOIN game_servers s ON s.id=r.server_id
               WHERE r.id=%s AND r.account_id=%s""",
            (run_id, account_id),
        ).fetchone()
        if row is None:
            raise HTTPException(status.HTTP_404_NOT_FOUND, "Run not found")
        if (row["status"] != "ready" or row["current_run_id"] != run_id or
                row["server_generation"] != row["generation"] or
                row["last_seen"] < datetime.now(timezone.utc) - timedelta(seconds=15)):
            raise HTTPException(status.HTTP_503_SERVICE_UNAVAILABLE, "Run server is not ready")
        return row

    @app.get("/health/live")
    def live():
        return {"status": "live"}

    @app.get("/health/ready")
    def ready():
        try:
            with connect() as db:
                versions = {row["version"] for row in db.execute("SELECT version FROM schema_migrations")}
                db.execute("SELECT 1")
            if not {"001_initial", "002_unavailable_server", "003_run_results",
                    "004_church_level_reward", "005_run_checkpoints", "006_online_backend", "007_weapons"}.issubset(versions):
                raise ValueError("Schema migration missing")
        except psycopg.Error as error:
            raise HTTPException(status.HTTP_503_SERVICE_UNAVAILABLE,
                                f"Database is not ready: {error.__class__.__name__}") from None
        except ValueError:
            raise HTTPException(status.HTTP_503_SERVICE_UNAVAILABLE, "Database schema is not ready") from None
        return {"status": "ready", "schemaVersion": "007_weapons"}

    @app.post("/v1/guests", status_code=201)
    def create_guest():
        account_id = uuid4()
        token = secrets.token_urlsafe(32)
        expires = datetime.now(timezone.utc) + timedelta(days=30)
        with connect() as db:
            with db.transaction():
                db.execute("INSERT INTO accounts(id) VALUES (%s)", (account_id,))
                db.execute("INSERT INTO profiles(account_id,version,display_name) VALUES (%s,1,'Guest')", (account_id,))
                db.execute(
                    "INSERT INTO guest_credentials(token_hash,account_id,expires_at) VALUES (%s,%s,%s)",
                    (hashlib.sha256(token.encode("ascii")).digest(), account_id, expires),
                )
        return {"accountId": str(account_id), "token": token, "expiresAt": expires.isoformat(),
                "recovery": "Token cannot be recovered if lost"}

    @app.get("/v1/profile")
    def profile(account_id: UUID = Depends(account_from_bearer)):
        with connect() as db:
            row = db.execute(
                "SELECT account_id,version,display_name,permanent_currency FROM profiles WHERE account_id=%s",
                (account_id,),
            ).fetchone()
        if row is None:
            raise HTTPException(status.HTTP_404_NOT_FOUND, "Profile not found")
        return {"accountId": str(row["account_id"]), "version": row["version"],
                "displayName": row["display_name"], "permanentCurrency": row["permanent_currency"]}

    @app.patch("/v1/profile")
    def patch_profile(body: ProfilePatch, account_id: UUID = Depends(account_from_bearer)):
        name = body.displayName.strip()
        if not name:
            raise HTTPException(status.HTTP_422_UNPROCESSABLE_ENTITY, "displayName is empty")
        if len(name.encode("utf-8")) > 120:
            raise HTTPException(status.HTTP_422_UNPROCESSABLE_ENTITY, "displayName is too long in UTF-8")
        with connect() as db:
            row = db.execute(
                """UPDATE profiles SET display_name=%s,version=version+1,updated_at=now()
                   WHERE account_id=%s AND version=%s
                   RETURNING version,display_name,permanent_currency""",
                (name, account_id, body.expectedVersion),
            ).fetchone()
        if row is None:
            raise HTTPException(status.HTTP_409_CONFLICT, "Profile version changed")
        return {"accountId": str(account_id), "version": row["version"],
                "displayName": row["display_name"], "permanentCurrency": row["permanent_currency"]}

    @app.post("/internal/servers/{server_id}/ready")
    def server_ready(body: ServerReady, server_id: UUID = Path(),
                     x_server_secret: str | None = Header(default=None)):
        slot = server_slot(server_id, x_server_secret)
        if body.transport != slot.transport:
            raise HTTPException(409, "Game transport does not match configured slot")
        if body.contentReleaseId not in settings.reward_policies:
            raise HTTPException(status.HTTP_422_UNPROCESSABLE_ENTITY,
                                "No approved reward policy for content release")
        with connect() as db:
            with db.transaction():
                existing = db.execute("SELECT current_run_id FROM game_servers WHERE id=%s FOR UPDATE", (server_id,)).fetchone()
                if existing is not None and existing["current_run_id"] is not None:
                    raise HTTPException(status.HTTP_409_CONFLICT, "Server already owns a run")
                db.execute(
                    """INSERT INTO game_servers(id,host,port,content_release_id,protocol_version,status,last_seen)
                       VALUES (%s,%s,%s,%s,%s,'ready',now())
                       ON CONFLICT(id) DO UPDATE SET host=EXCLUDED.host,port=EXCLUDED.port,
                         content_release_id=EXCLUDED.content_release_id,protocol_version=EXCLUDED.protocol_version,
                         status='ready',last_seen=now()""",
                    (server_id, slot.host, slot.port, body.contentReleaseId, body.protocolVersion),
                )
        return {"status": "ready"}

    @app.get("/internal/servers/{server_id}/assignment")
    def server_assignment(server_id: UUID, x_server_secret: str | None = Header(default=None)):
        server_slot(server_id, x_server_secret)
        with connect() as db:
            with db.transaction():
                server = db.execute(
                    "UPDATE game_servers SET last_seen=now() WHERE id=%s RETURNING current_run_id",
                    (server_id,),
                ).fetchone()
                if server is None:
                    raise HTTPException(status.HTTP_409_CONFLICT, "Server has not registered")
                if server["current_run_id"] is None:
                    return {"assigned": False}
                run = db.execute(
                    """SELECT id,account_id,content_release_id,profile_version,profile_display_name,generation,status,weapon_snapshot
                       FROM runs WHERE id=%s AND server_id=%s""",
                    (server["current_run_id"], server_id),
                ).fetchone()
        if run is None or run["status"] == "failed":
            return {"assigned": False}
        return {"assigned": True, "runId": str(run["id"]), "accountId": str(run["account_id"]),
                "contentReleaseId": run["content_release_id"], "profileVersion": run["profile_version"],
                "displayName": run["profile_display_name"], "generation": run["generation"],
                "weaponsJson": json.dumps(run["weapon_snapshot"])}

    @app.post("/internal/servers/{server_id}/ack")
    def server_ack(body: AssignmentAck, server_id: UUID,
                   x_server_secret: str | None = Header(default=None)):
        server_slot(server_id, x_server_secret)
        with connect() as db:
            with db.transaction():
                row = db.execute(
                    """UPDATE runs SET status='ready' WHERE id=%s AND server_id=%s AND generation=%s
                       AND status='pending' RETURNING id""",
                    (body.runId, server_id, body.generation),
                ).fetchone()
                if row is None:
                    raise HTTPException(status.HTTP_409_CONFLICT, "Assignment is stale")
                server = db.execute(
                    """UPDATE game_servers SET status='occupied',last_seen=now()
                       WHERE id=%s AND current_run_id=%s AND generation=%s RETURNING id""",
                    (server_id, body.runId, body.generation),
                ).fetchone()
                if server is None:
                    raise HTTPException(status.HTTP_409_CONFLICT, "Server assignment changed")
        return {"status": "ready"}

    @app.post("/internal/servers/{server_id}/heartbeat")
    def server_heartbeat(body: AssignmentAck, server_id: UUID,
                         x_server_secret: str | None = Header(default=None)):
        server_slot(server_id, x_server_secret)
        with connect() as db:
            row = db.execute(
                """UPDATE game_servers SET last_seen=now() WHERE id=%s AND current_run_id=%s
                   AND generation=%s AND status='occupied' RETURNING id""",
                (server_id, body.runId, body.generation),
            ).fetchone()
        if row is None:
            raise HTTPException(status.HTTP_409_CONFLICT, "Assignment is no longer current")
        return {"current": True}

    @app.post("/internal/servers/{server_id}/checkpoint")
    def publish_checkpoint(body: CheckpointPublish, server_id: UUID,
                           x_server_secret: str | None = Header(default=None)):
        server_slot(server_id, x_server_secret)
        payload = body.snapshotJson.encode("utf-8")
        digest = hashlib.sha256(payload).digest()
        if not hmac.compare_digest(digest.hex(), body.sha256):
            raise HTTPException(status.HTTP_422_UNPROCESSABLE_ENTITY, "Checkpoint checksum mismatch")
        try:
            snapshot = json.loads(body.snapshotJson)
        except (ValueError, TypeError):
            raise HTTPException(status.HTTP_422_UNPROCESSABLE_ENTITY, "Checkpoint JSON is invalid") from None
        if (not isinstance(snapshot, dict) or snapshot.get("schemaVersion") != body.schemaVersion or
                snapshot.get("runId") != str(body.runId) or
                snapshot.get("simulationTick") != body.simulationTick):
            raise HTTPException(status.HTTP_422_UNPROCESSABLE_ENTITY, "Checkpoint identity mismatch")
        with connect() as db:
            with db.transaction():
                run = db.execute(
                    """SELECT r.generation,r.status,r.content_release_id,s.generation AS server_generation,
                              s.current_run_id,s.status AS server_status
                       FROM runs r JOIN game_servers s ON s.id=r.server_id
                       WHERE r.id=%s AND r.server_id=%s FOR UPDATE OF r,s""",
                    (body.runId, server_id),
                ).fetchone()
                if (run is None or run["generation"] != body.generation or
                        run["server_generation"] != body.generation or
                        run["current_run_id"] != body.runId or run["status"] != "ready" or
                        run["server_status"] != "occupied" or
                        snapshot.get("contentReleaseId") != run["content_release_id"]):
                    raise HTTPException(status.HTTP_409_CONFLICT, "Checkpoint owner is no longer current")
                prior = db.execute("SELECT simulation_tick,sha256 FROM run_checkpoints WHERE run_id=%s FOR UPDATE",
                                   (body.runId,)).fetchone()
                if prior is not None and body.simulationTick < prior["simulation_tick"]:
                    raise HTTPException(status.HTTP_409_CONFLICT, "Checkpoint tick moved backwards")
                if (prior is not None and body.simulationTick == prior["simulation_tick"] and
                        hmac.compare_digest(prior["sha256"], digest)):
                    return {"accepted": True, "simulationTick": body.simulationTick,
                            "replayed": True}
                db.execute(
                    """INSERT INTO run_checkpoints(run_id,generation,schema_version,simulation_tick,
                                                   snapshot_json,sha256)
                       VALUES (%s,%s,%s,%s,%s,%s)
                       ON CONFLICT(run_id) DO UPDATE SET generation=EXCLUDED.generation,
                           schema_version=EXCLUDED.schema_version,
                           simulation_tick=EXCLUDED.simulation_tick,
                           snapshot_json=EXCLUDED.snapshot_json,sha256=EXCLUDED.sha256,
                           created_at=now()""",
                    (body.runId, body.generation, body.schemaVersion, body.simulationTick,
                     body.snapshotJson, digest),
                )
        return {"accepted": True, "simulationTick": body.simulationTick}

    @app.post("/internal/servers/{server_id}/recover")
    def recover_checkpoint(body: ServerReady, server_id: UUID,
                           x_server_secret: str | None = Header(default=None)):
        slot = server_slot(server_id, x_server_secret)
        if body.transport != slot.transport:
            raise HTTPException(409, "Game transport does not match configured slot")
        with connect() as db:
            with db.transaction():
                server = db.execute("SELECT * FROM game_servers WHERE id=%s FOR UPDATE",
                                    (server_id,)).fetchone()
                if server is None or server["current_run_id"] is None:
                    return {"assigned": False}
                if server["last_seen"] > datetime.now(timezone.utc) - timedelta(seconds=15):
                    return JSONResponse(status_code=202, content={"assigned": False, "recoveryPending": True})
                run = db.execute("SELECT * FROM runs WHERE id=%s AND server_id=%s FOR UPDATE",
                                 (server["current_run_id"], server_id)).fetchone()
                checkpoint = db.execute("SELECT * FROM run_checkpoints WHERE run_id=%s",
                                        (server["current_run_id"],)).fetchone()
                if (run is None or run["status"] not in ("ready", "pending") or checkpoint is None or
                        checkpoint["generation"] > run["generation"] or
                        server["generation"] != run["generation"]):
                    raise HTTPException(status.HTTP_409_CONFLICT,
                                        "No recoverable checkpoint for assigned run")
                if (run["content_release_id"] != body.contentReleaseId or
                        server["protocol_version"] != body.protocolVersion or
                        run["content_release_id"] != server["content_release_id"] or
                        server["host"] != slot.host or server["port"] != slot.port):
                    raise HTTPException(status.HTTP_409_CONFLICT, "Recovery content or server slot changed")
                if (checkpoint["schema_version"] != 4 or
                        hashlib.sha256(checkpoint["snapshot_json"].encode("utf-8")).digest() !=
                        checkpoint["sha256"]):
                    raise HTTPException(status.HTTP_409_CONFLICT, "Stored checkpoint is corrupt or unsupported")
                generation = run["generation"] + 1
                db.execute("UPDATE runs SET generation=%s,status='pending' WHERE id=%s",
                           (generation, run["id"]))
                db.execute("UPDATE game_servers SET generation=%s,status='assigned',last_seen=now() "
                           "WHERE id=%s", (generation, server_id))
        return {"assigned": True, "runId": str(run["id"]),
                "accountId": str(run["account_id"]),
                "contentReleaseId": run["content_release_id"],
                "profileVersion": run["profile_version"],
                "displayName": run["profile_display_name"],
                "generation": generation,
                "snapshotJson": checkpoint["snapshot_json"],
                "weaponsJson": json.dumps(run["weapon_snapshot"])}

    @app.post("/internal/servers/{server_id}/abandon")
    def abandon_run(body: AssignmentAck, server_id: UUID,
                    x_server_secret: str | None = Header(default=None)):
        server_slot(server_id, x_server_secret)
        with connect() as db:
            with db.transaction():
                run = db.execute(
                    "SELECT status FROM runs WHERE id=%s AND server_id=%s AND generation=%s FOR UPDATE",
                    (body.runId, server_id, body.generation),
                ).fetchone()
                if run is None:
                    raise HTTPException(status.HTTP_409_CONFLICT, "Run is not active")
                if run["status"] == "failed":
                    return {"status": "abandoned", "replayed": True}
                if run["status"] != "ready":
                    raise HTTPException(status.HTTP_409_CONFLICT, "Run is not active")
                server = db.execute(
                    """UPDATE game_servers SET status='unavailable',current_run_id=NULL
                       WHERE id=%s AND current_run_id=%s AND generation=%s AND status='occupied'
                       RETURNING id""",
                    (server_id, body.runId, body.generation),
                ).fetchone()
                if server is None:
                    raise HTTPException(status.HTTP_409_CONFLICT, "Run assignment is no longer current")
                db.execute("UPDATE runs SET status='failed' WHERE id=%s", (body.runId,))
        return {"status": "abandoned"}

    @app.post("/internal/servers/{server_id}/results")
    def apply_result(body: RunResult, server_id: UUID,
                     x_server_secret: str | None = Header(default=None)):
        server_slot(server_id, x_server_secret)
        canonical = json.dumps(body.model_dump(mode="json"), sort_keys=True,
                               separators=(",", ":")).encode("utf-8")
        digest = hashlib.sha256(canonical).digest()
        with connect() as db:
            with db.transaction():
                db.execute("SELECT id FROM accounts WHERE id=%s FOR UPDATE", (body.accountId,))
                run = db.execute(
                    """SELECT r.*, s.current_run_id, s.generation AS server_generation
                       FROM runs r JOIN game_servers s ON s.id=r.server_id
                       WHERE r.id=%s FOR UPDATE OF r, s""",
                    (body.runId,),
                ).fetchone()
                if (run is None or run["server_id"] != server_id or
                        run["account_id"] != body.accountId or
                        run["content_release_id"] != body.contentReleaseId or
                        run["generation"] != body.generation):
                    raise HTTPException(status.HTTP_409_CONFLICT, "Result assignment mismatch")
                prior = db.execute(
                    "SELECT payload_hash,outcome,permanent_reward,profile_version "
                    "FROM run_result_operations WHERE run_id=%s", (body.runId,)
                ).fetchone()
                if prior is not None:
                    if not hmac.compare_digest(prior["payload_hash"], digest):
                        raise HTTPException(status.HTTP_409_CONFLICT,
                                            "Conflicting result for this RunId")
                    return {"runId": str(body.runId), "outcome": prior["outcome"],
                            "permanentReward": prior["permanent_reward"],
                            "profileVersion": prior["profile_version"], "replayed": True}
                if (run["status"] != "ready" or run["current_run_id"] != body.runId or
                        run["server_generation"] != body.generation):
                    raise HTTPException(status.HTTP_409_CONFLICT, "Run assignment is no longer current")
                rate = run["reward_per_church_level"]
                if rate is None:
                    raise HTTPException(status.HTTP_422_UNPROCESSABLE_ENTITY,
                                        "No pinned reward policy for content release")
                GrantMatchWeapon(db, body.accountId, body.runId, weapon_catalog)
                reward = rate * body.churchLevel
                profile = db.execute(
                    """UPDATE profiles SET permanent_currency=permanent_currency+%s,
                           version=version+1,updated_at=now() WHERE account_id=%s
                           RETURNING version""", (reward, body.accountId)
                ).fetchone()
                db.execute(
                    """INSERT INTO run_result_operations
                       (run_id,account_id,server_id,generation,content_release_id,
                        payload_hash,outcome,permanent_reward,profile_version,church_level)
                       VALUES (%s,%s,%s,%s,%s,%s,%s,%s,%s,%s)""",
                    (body.runId, body.accountId, server_id, body.generation,
                     body.contentReleaseId, digest, body.outcome, reward, profile["version"],
                     body.churchLevel),
                )
                db.execute(
                    """UPDATE runs SET status='completed',outcome=%s,permanent_reward=%s,
                       completed_at=now() WHERE id=%s""", (body.outcome, reward, body.runId)
                )
                db.execute(
                    """UPDATE game_servers SET current_run_id=NULL,status='unavailable',
                       last_seen=now() WHERE id=%s AND current_run_id=%s""",
                    (server_id, body.runId),
                )
        return {"runId": str(body.runId), "outcome": body.outcome,
                "permanentReward": reward, "profileVersion": profile["version"],
                "replayed": False}

    @app.post("/v1/runs")
    def assign_run(account_id: UUID = Depends(account_from_bearer),
                   prefer: str | None = Header(default=None)):
        no_server = False
        with connect() as db:
            with db.transaction():
                db.execute("SELECT id FROM accounts WHERE id=%s FOR UPDATE", (account_id,))
                existing = db.execute(
                    """SELECT r.id,r.status,r.created_at,s.id AS server_id,s.last_seen,s.current_run_id
                       FROM runs r LEFT JOIN game_servers s ON s.id=r.server_id
                       WHERE r.account_id=%s AND r.status IN ('pending','ready')
                       ORDER BY r.created_at DESC LIMIT 1 FOR UPDATE OF r""",
                    (account_id,),
                ).fetchone()
                if existing is not None and (
                    existing["server_id"] is None or
                    existing["current_run_id"] != existing["id"] or
                    (existing["status"] == "ready" and
                     (existing["last_seen"] is None or
                      existing["last_seen"] <= datetime.now(timezone.utc) - timedelta(seconds=15))) or
                    (existing["status"] == "pending" and
                     existing["created_at"] <= datetime.now(timezone.utc) - timedelta(seconds=30))
                ):
                    checkpoint = db.execute(
                        "SELECT 1 FROM run_checkpoints WHERE run_id=%s", (existing["id"],)
                    ).fetchone()
                    if checkpoint is not None and existing["server_id"] is not None:
                        raise HTTPException(status.HTTP_503_SERVICE_UNAVAILABLE,
                                            "Run is awaiting checkpoint recovery")
                    db.execute("UPDATE runs SET status='failed' WHERE id=%s", (existing["id"],))
                    db.execute(
                        """UPDATE game_servers SET status='unavailable',current_run_id=NULL
                           WHERE id=%s AND current_run_id=%s""",
                        (existing["server_id"], existing["id"]),
                    )
                    existing = None
                if existing is not None:
                    run_id = existing["id"]
                else:
                    server = db.execute(
                        """SELECT id,content_release_id,generation FROM game_servers
                           WHERE status='ready' AND current_run_id IS NULL
                             AND last_seen>now()-interval '15 seconds'
                           ORDER BY id FOR UPDATE SKIP LOCKED LIMIT 1"""
                    ).fetchone()
                    if server is None:
                        no_server = True
                    else:
                        profile_row = db.execute(
                            "SELECT version,display_name FROM profiles WHERE account_id=%s", (account_id,)
                        ).fetchone()
                        run_id = uuid4()
                        generation = server["generation"] + 1
                        policy = settings.reward_policies.get(server["content_release_id"])
                        db.execute(
                            """INSERT INTO runs(id,account_id,server_id,content_release_id,profile_version,
                               profile_display_name,generation,status,reward_per_church_level)
                               VALUES (%s,%s,%s,%s,%s,%s,%s,'pending',%s)""",
                            (run_id, account_id, server["id"], server["content_release_id"],
                             profile_row["version"], profile_row["display_name"], generation,
                             policy.per_church_level if policy else None),
                        )
                        PinLoadout(db, account_id, run_id, weapon_catalog)
                        db.execute(
                            """UPDATE game_servers SET status='assigned',current_run_id=%s,generation=%s
                               WHERE id=%s""",
                            (run_id, generation, server["id"]),
                        )

        if no_server:
            raise HTTPException(status.HTTP_503_SERVICE_UNAVAILABLE,
                                "No live game process is available")

        if prefer == "respond-async":
            return JSONResponse(status_code=202, content={"runId": str(run_id), "status": "pending"},
                                headers={"Location": f"/v1/runs/{run_id}", "Retry-After": "1"})

        deadline = time.monotonic() + 20
        while time.monotonic() < deadline:
            with connect() as db:
                row = db.execute("SELECT status FROM runs WHERE id=%s", (run_id,)).fetchone()
                if row is not None and row["status"] == "ready":
                    return grant_response(ready_run(db, run_id, account_id))
                if row is None or row["status"] == "failed":
                    break
            time.sleep(0.2)
        with connect() as db:
            with db.transaction():
                db.execute("UPDATE runs SET status='failed' WHERE id=%s AND status='pending'", (run_id,))
                db.execute(
                    """UPDATE game_servers SET status='ready',current_run_id=NULL
                       WHERE current_run_id=%s AND status='assigned'""",
                    (run_id,),
                )
        raise HTTPException(status.HTTP_503_SERVICE_UNAVAILABLE, "Game process did not confirm the assignment")

    @app.post("/v1/runs/{run_id}/grant")
    def renew_grant(run_id: UUID, account_id: UUID = Depends(account_from_bearer)):
        with connect() as db:
            return grant_response(ready_run(db, run_id, account_id))

    @app.get("/v1/runs/{run_id}")
    def assignment_status(run_id: UUID, account_id: UUID = Depends(account_from_bearer)):
        with connect() as db:
            row = db.execute("SELECT status FROM runs WHERE id=%s AND account_id=%s",
                             (run_id, account_id)).fetchone()
            if row is None:
                raise HTTPException(404, "Run not found")
            if row["status"] == "pending":
                return JSONResponse(status_code=202, content={"runId": str(run_id), "status": "pending"},
                                    headers={"Retry-After": "1"})
            return grant_response(ready_run(db, run_id, account_id))

    from .weapons import InstallWeapons
    InstallWeapons(app, connect, account_from_bearer, weapon_catalog)
    from .accounts import install_accounts
    from .shop import install_shop
    from .content import install_content
    install_accounts(app, connect, account_from_bearer)
    install_shop(app, connect, account_from_bearer, settings.shop_catalog)
    install_content(app, connect, settings.content_admin_secret)

    return app
