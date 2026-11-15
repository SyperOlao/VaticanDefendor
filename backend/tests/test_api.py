import os
import hashlib
import json
import secrets
import time
from concurrent.futures import ThreadPoolExecutor
from uuid import uuid4

import psycopg
import pytest
from fastapi.testclient import TestClient

from vatican_backend.api import create_app
from vatican_backend.migrate import migrate
from vatican_backend.settings import RewardPolicy, ServerSlot, Settings


@pytest.fixture()
def backend():
    url = os.environ.get("VD_TEST_DATABASE_URL")
    if not url:
        pytest.skip("Set VD_TEST_DATABASE_URL to a disposable PostgreSQL database")
    migrate(url)
    with psycopg.connect(url) as db:
        db.execute("TRUNCATE guest_credentials, profiles, runs, game_servers, accounts, auth_attempts, content_releases CASCADE")
    server_id = uuid4()
    slot = ServerSlot(server_id, "127.0.0.1", 7979, secrets.token_urlsafe(32))
    other_slot = ServerSlot(uuid4(), "127.0.0.1", 7980, secrets.token_urlsafe(32))
    settings = Settings(url, {server_id: slot, other_slot.server_id: other_slot}, 120,
                        {"sha256-test-release": RewardPolicy(25)})
    with TestClient(create_app(settings)) as client:
        yield client, settings, slot


def bearer(token):
    return {"Authorization": f"Bearer {token}"}


def server_header(slot):
    return {"X-Server-Secret": slot.secret}


def assigned_run(client, slot, guest):
    assert client.post(f"/internal/servers/{slot.server_id}/ready", headers=server_header(slot),
                       json={"contentReleaseId": "sha256-test-release", "protocolVersion": 3}).status_code == 200
    with ThreadPoolExecutor(max_workers=1) as pool:
        future = pool.submit(lambda: client.post("/v1/runs", headers=bearer(guest["token"])))
        pending = None
        for _ in range(60):
            response = client.get(f"/internal/servers/{slot.server_id}/assignment",
                                  headers=server_header(slot))
            if response.json()["assigned"]:
                pending = response.json()
                break
            time.sleep(0.05)
        assert pending is not None
        assert client.post(f"/internal/servers/{slot.server_id}/ack",
                           headers=server_header(slot),
                           json={"runId": pending["runId"],
                                 "generation": pending["generation"]}).status_code == 200
        assert future.result(timeout=5).status_code == 200
    return pending


def result_body(pending, outcome="victory"):
    return {"runId": pending["runId"], "accountId": pending["accountId"],
            "contentReleaseId": pending["contentReleaseId"],
            "generation": pending["generation"], "outcome": outcome,
            "churchLevel": 1}


def checkpoint_body(pending, tick=10):
    snapshot = {"schemaVersion": 4, "runId": pending["runId"],
                "accountId": pending["accountId"],
                "contentReleaseId": pending["contentReleaseId"],
                "simulationTick": tick}
    text = json.dumps(snapshot, separators=(",", ":"))
    return {"runId": pending["runId"], "generation": pending["generation"],
            "schemaVersion": 4, "simulationTick": tick, "snapshotJson": text,
            "sha256": hashlib.sha256(text.encode()).hexdigest()}


def test_checkpoint_recovery_fences_old_generation(backend):
    client, settings, slot = backend
    guest = client.post("/v1/guests").json()
    run = assigned_run(client, slot, guest)
    url = f"/internal/servers/{slot.server_id}/checkpoint"
    body = checkpoint_body(run)
    assert client.post(url, headers=bearer(guest["token"]), json=body).status_code == 401
    corrupt = dict(body, sha256="0" * 64)
    assert client.post(url, headers=server_header(slot), json=corrupt).status_code == 422
    assert client.post(url, headers=server_header(slot), json=body).status_code == 200
    assert client.post(url, headers=server_header(slot), json=body).json()["replayed"] is True
    changed = dict(body)
    changed["snapshotJson"] = body["snapshotJson"][:-1] + ',"manualPaused":1}'
    changed["sha256"] = hashlib.sha256(changed["snapshotJson"].encode()).hexdigest()
    assert client.post(url, headers=server_header(slot), json=changed).status_code == 200
    assert client.post(url, headers=server_header(slot),
                       json=checkpoint_body(run, 9)).status_code == 409
    body = changed
    pending_recovery = client.post(f"/internal/servers/{slot.server_id}/recover",
                                   headers=server_header(slot),
                                   json={"contentReleaseId": "sha256-test-release", "protocolVersion": 3})
    assert pending_recovery.status_code == 202 and pending_recovery.json()["recoveryPending"]
    assert client.post(url, headers=server_header(slot), json=dict(body, schemaVersion=1)).status_code == 422
    with psycopg.connect(settings.database_url) as db:
        db.execute("UPDATE game_servers SET last_seen=now()-interval '60 seconds' WHERE id=%s",
                   (slot.server_id,))
    assert client.post("/v1/runs", headers=bearer(guest["token"])).status_code == 503
    with psycopg.connect(settings.database_url) as db:
        assert db.execute("SELECT status FROM runs WHERE id=%s",
                          (run["runId"],)).fetchone()[0] == "ready"
    recover_url = f"/internal/servers/{slot.server_id}/recover"
    assert client.post(recover_url, headers=bearer(guest["token"]),
                       json={"contentReleaseId": "sha256-test-release",
                             "protocolVersion": 3}).status_code == 401
    wrong = client.post(recover_url, headers=server_header(slot),
                        json={"contentReleaseId": "wrong-release", "protocolVersion": 3})
    assert wrong.status_code == 409
    with psycopg.connect(settings.database_url) as db:
        db.execute("UPDATE run_checkpoints SET snapshot_json='{}' WHERE run_id=%s",
                   (run["runId"],))
    assert client.post(recover_url, headers=server_header(slot),
                       json={"contentReleaseId": "sha256-test-release",
                             "protocolVersion": 3}).status_code == 409
    with psycopg.connect(settings.database_url) as db:
        db.execute("UPDATE run_checkpoints SET snapshot_json=%s WHERE run_id=%s",
                   (body["snapshotJson"], run["runId"]))
    recovered = client.post(recover_url, headers=server_header(slot),
                            json={"contentReleaseId": "sha256-test-release",
                                  "protocolVersion": 3})
    assert recovered.status_code == 200
    assert recovered.json()["runId"] == run["runId"]
    assert recovered.json()["generation"] == run["generation"] + 1
    assert recovered.json()["snapshotJson"] == body["snapshotJson"]
    assert client.post(url, headers=server_header(slot),
                       json=checkpoint_body(run, 11)).status_code == 409
    assert client.post(f"/internal/servers/{slot.server_id}/ack", headers=server_header(slot),
                       json={"runId": run["runId"],
                             "generation": recovered.json()["generation"]}).status_code == 200
    assert client.post(f"/v1/runs/{run['runId']}/grant",
                       headers=bearer(guest["token"])).status_code == 200
    new_body = checkpoint_body({**run, "generation": recovered.json()["generation"]}, 11)
    assert client.post(url, headers=server_header(slot), json=new_body).status_code == 200
    result = result_body({**run, "generation": recovered.json()["generation"]})
    result_url = f"/internal/servers/{slot.server_id}/results"
    assert client.post(result_url, headers=server_header(slot),
                       json=result_body(run)).status_code == 409
    assert client.post(result_url, headers=server_header(slot), json=result).json()[
        "permanentReward"] == 25
    assert client.post(result_url, headers=server_header(slot), json=result).json()[
        "replayed"] is True
    with psycopg.connect(settings.database_url) as db:
        assert db.execute("SELECT count(*) FROM run_result_operations WHERE run_id=%s",
                          (run["runId"],)).fetchone()[0] == 1


def test_concurrent_result_replay_conflict_and_authority(backend):
    client, settings, slot = backend
    guest = client.post("/v1/guests").json()
    pending = assigned_run(client, slot, guest)
    url = f"/internal/servers/{slot.server_id}/results"
    body = result_body(pending)
    body["churchLevel"] = 2
    assert client.post(url, headers=bearer(guest["token"]), json=body).status_code == 401
    with ThreadPoolExecutor(max_workers=2) as pool:
        responses = list(pool.map(lambda _: client.post(url, headers=server_header(slot),
                                                        json=body), range(2)))
    assert all(response.status_code == 200 for response in responses)
    assert sorted(response.json()["replayed"] for response in responses) == [False, True]
    assert all(response.json()["permanentReward"] == 50 for response in responses)
    assert client.post(url, headers=server_header(slot), json=body).json()["replayed"] is True
    assert client.post(url, headers=server_header(slot),
                       json=result_body(pending, "defeat")).status_code == 409
    assert client.post(url, headers=server_header(slot),
                       json=result_body(pending, "victory")).status_code == 409
    profile = client.get("/v1/profile", headers=bearer(guest["token"])).json()
    assert profile["permanentCurrency"] == 550 and profile["version"] == 2
    with TestClient(create_app(settings)) as restarted:
        assert restarted.get("/v1/profile", headers=bearer(guest["token"])).json()[
            "permanentCurrency"] == 550
        assert restarted.post(url, headers=server_header(slot), json=body).json()[
            "replayed"] is True
    with psycopg.connect(settings.database_url) as db:
        assert db.execute("SELECT count(*) FROM run_result_operations WHERE run_id=%s",
                          (pending["runId"],)).fetchone()[0] == 1
        assert db.execute("SELECT status FROM runs WHERE id=%s",
                          (pending["runId"],)).fetchone()[0] == "completed"


def test_result_requires_current_assignment(backend):
    client, settings, slot = backend
    guest = client.post("/v1/guests").json()
    pending = assigned_run(client, slot, guest)
    with psycopg.connect(settings.database_url) as db:
        db.execute("UPDATE game_servers SET generation=generation+1 WHERE id=%s",
                   (slot.server_id,))
    response = client.post(f"/internal/servers/{slot.server_id}/results",
                           headers=server_header(slot), json=result_body(pending))
    assert response.status_code == 409
    assert client.get("/v1/profile", headers=bearer(guest["token"])).json()[
        "permanentCurrency"] == 500


def test_reconnect_grant_and_retention_abandon(backend):
    client, settings, slot = backend
    owner = client.post("/v1/guests").json()
    stranger = client.post("/v1/guests").json()
    pending = assigned_run(client, slot, owner)
    grant_url = f"/v1/runs/{pending['runId']}/grant"
    renewed = client.post(grant_url, headers=bearer(owner["token"]))
    assert renewed.status_code == 200
    assert renewed.json()["runId"] == pending["runId"]
    assert renewed.json()["serverId"] == str(slot.server_id)
    assert client.post(grant_url, headers=bearer(stranger["token"])).status_code == 404
    abandon_url = f"/internal/servers/{slot.server_id}/abandon"
    body = {"runId": pending["runId"], "generation": pending["generation"]}
    assert client.post(abandon_url, headers=bearer(owner["token"]), json=body).status_code == 401
    assert client.post(abandon_url, headers=server_header(slot), json=body).status_code == 200
    assert client.post(grant_url, headers=bearer(owner["token"])).status_code == 503
    replay = client.post(abandon_url, headers=server_header(slot), json=body)
    assert replay.status_code == 200 and replay.json()["replayed"] is True
    with psycopg.connect(settings.database_url) as db:
        assert db.execute("SELECT status FROM runs WHERE id=%s",
                          (pending["runId"],)).fetchone()[0] == "failed"


def test_stale_run_cleanup_commits_without_spare_server(backend):
    client, settings, slot = backend
    guest = client.post("/v1/guests").json()
    pending = assigned_run(client, slot, guest)
    with psycopg.connect(settings.database_url) as db:
        db.execute("UPDATE game_servers SET last_seen=now()-interval '60 seconds' WHERE id=%s",
                   (slot.server_id,))
    assert client.post("/v1/runs", headers=bearer(guest["token"])).status_code == 503
    with psycopg.connect(settings.database_url) as db:
        assert db.execute("SELECT status FROM runs WHERE id=%s",
                          (pending["runId"],)).fetchone()[0] == "failed"
        assert db.execute("SELECT current_run_id FROM game_servers WHERE id=%s",
                          (slot.server_id,)).fetchone()[0] is None


def test_release_without_approved_reward_policy_cannot_register(backend):
    _, settings, slot = backend
    without_policy = Settings(settings.database_url, settings.servers,
                              settings.grant_ttl_seconds)
    with TestClient(create_app(without_policy)) as client:
        response = client.post(f"/internal/servers/{slot.server_id}/ready",
                               headers=server_header(slot),
                               json={"contentReleaseId": "sha256-test-release",
                                     "protocolVersion": 4})
        assert response.status_code == 422


def test_guest_profile_assignment_and_authority(backend):
    client, settings, slot = backend
    a = client.post("/v1/guests").json()
    b = client.post("/v1/guests").json()
    assert a["accountId"] != b["accountId"]
    assert client.get("/v1/profile", headers=bearer(a["token"])).json()["version"] == 1
    assert client.patch("/v1/profile", headers=bearer(a["token"]),
                        json={"expectedVersion": 1, "displayName": "Anna"}).json()["version"] == 2
    assert client.patch("/v1/profile", headers=bearer(a["token"]),
                        json={"expectedVersion": 1, "displayName": "Stale"}).status_code == 409
    assert client.patch("/v1/profile", headers=bearer(a["token"]),
                        json={"expectedVersion": 2, "displayName": "Hack", "permanentCurrency": 999}).status_code == 422
    assert client.get("/v1/profile", headers=bearer(b["token"])).json()["displayName"] == "Guest"

    with TestClient(create_app(settings)) as restarted:
        assert restarted.get("/v1/profile", headers=bearer(a["token"])).json()["displayName"] == "Anna"

    assert client.post("/v1/runs", headers=bearer(a["token"])).status_code == 503
    ready = client.post(f"/internal/servers/{slot.server_id}/ready", headers=server_header(slot),
                        json={"contentReleaseId": "sha256-test-release", "protocolVersion": 2})
    assert ready.status_code == 200, ready.text
    assert client.post(f"/internal/servers/{slot.server_id}/heartbeat",
                       headers=bearer(a["token"]),
                       json={"runId": str(uuid4()), "generation": 1}).status_code == 401

    with ThreadPoolExecutor(max_workers=1) as pool:
        assignment = pool.submit(lambda: client.post("/v1/runs", headers=bearer(a["token"])))
        pending = None
        for _ in range(50):
            response = client.get(f"/internal/servers/{slot.server_id}/assignment", headers=server_header(slot))
            assert response.status_code == 200, response.text
            if response.json()["assigned"]:
                pending = response.json()
                break
            time.sleep(0.05)
        assert pending is not None
        assert pending["profileVersion"] == 2
        assert pending["displayName"] == "Anna"
        ack = client.post(f"/internal/servers/{slot.server_id}/ack", headers=server_header(slot),
                          json={"runId": pending["runId"], "generation": pending["generation"]})
        assert ack.status_code == 200, ack.text
        assigned = assignment.result(timeout=5)
    assert assigned.status_code == 200, assigned.text
    grant = assigned.json()
    assert grant["runId"] == pending["runId"]
    assert grant["serverId"] == str(slot.server_id)
    assert grant["profileVersion"] == 2
    assert grant["profileDisplayName"] == "Anna"
    assert len(grant["grant"]) <= 128
    assert client.post(f"/v1/runs/{grant['runId']}/grant", headers=bearer(b["token"])).status_code == 404
    assert client.post(f"/v1/runs/{grant['runId']}/grant", headers=bearer(a["token"])).status_code == 200
    assert client.post(f"/internal/servers/{slot.server_id}/heartbeat", headers=server_header(slot),
                       json={"runId": pending["runId"], "generation": pending["generation"]}).status_code == 200
    assert client.post(f"/internal/servers/{slot.server_id}/ack", headers=bearer(a["token"]),
                       json={"runId": pending["runId"], "generation": pending["generation"]}).status_code == 401

    with psycopg.connect(settings.database_url) as db:
        db.execute("""UPDATE game_servers SET generation=generation+1,
                   last_seen=now()-interval '1 minute' WHERE id=%s""", (slot.server_id,))
    assert client.post(f"/v1/runs/{grant['runId']}/grant", headers=bearer(a["token"])).status_code == 503
    other_slot = next(value for key, value in settings.servers.items() if key != slot.server_id)
    assert client.post(f"/internal/servers/{other_slot.server_id}/ready", headers=server_header(other_slot),
                       json={"contentReleaseId": "sha256-test-release", "protocolVersion": 2}).status_code == 200
    with ThreadPoolExecutor(max_workers=1) as pool:
        reassignment = pool.submit(lambda: client.post("/v1/runs", headers=bearer(a["token"])))
        pending = None
        for _ in range(50):
            response = client.get(f"/internal/servers/{other_slot.server_id}/assignment",
                                  headers=server_header(other_slot))
            if response.json()["assigned"]:
                pending = response.json()
                break
            time.sleep(0.05)
        assert pending is not None, reassignment.result(timeout=1).text if reassignment.done() else "No assignment"
        assert client.post(f"/internal/servers/{other_slot.server_id}/ack",
                           headers=server_header(other_slot),
                           json={"runId": pending["runId"], "generation": pending["generation"]}).status_code == 200
        new_run = reassignment.result(timeout=5)
    assert new_run.status_code == 200, new_run.text
    assert new_run.json()["runId"] != grant["runId"]
    assert new_run.json()["serverId"] == str(other_slot.server_id)
    with psycopg.connect(settings.database_url) as db:
        db.execute("UPDATE guest_credentials SET expires_at=now()-interval '1 second' WHERE account_id=%s",
                   (a["accountId"],))
    assert client.get("/v1/profile", headers=bearer(a["token"])).status_code == 401
