import ast
from concurrent.futures import ThreadPoolExecutor
from dataclasses import replace
from pathlib import Path
from threading import Event
from uuid import UUID

from fastapi.testclient import TestClient
import psycopg
from psycopg import sql
import pytest

from .support import assigned_run, game_app, player_for, result_body, server_header
from vatican_backend.api import create_app
from vatican_backend.application.commands import Assignment
from vatican_backend.application.errors import ApplicationError
from vatican_backend.persistence.postgres.repositories.runs import RunsRepository


def test_http_does_not_query_the_database():
    root = Path(__file__).parents[1] / "vatican_backend"
    for path in (root / "presentation").glob("*.py"):
        name = path.name
        for node in ast.walk(ast.parse(path.read_text())):
            if isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute):
                assert node.func.attr not in {"exec_driver_sql", "scalar", "scalars", "transaction", "connection"}, name


def test_temporarily_locked_ready_server_is_retried_outside_transaction(backend, monkeypatch):
    client, settings, slot = backend
    player = player_for(client)
    assert client.post(f"/internal/servers/{slot.server_id}/ready", headers=server_header(slot),
                       json={"contentReleaseId": "sha256-test-release", "protocolVersion": 3}).status_code == 200
    skipped = Event()
    original = RunsRepository.has_available_server

    def available(repository):
        result = original(repository)
        if result:
            skipped.set()
        return result

    monkeypatch.setattr(RunsRepository, "has_available_server", available)
    with ThreadPoolExecutor(max_workers=1) as workers:
        with psycopg.connect(settings.database_url) as connection:
            connection.execute("SELECT id FROM game_servers WHERE id=%s FOR UPDATE", (slot.server_id,))
            pending = workers.submit(client.app.state.use_cases.runs.assign, UUID(player["accountId"]), respond_async=True)
            assert skipped.wait(timeout=2)
        result = pending.result(timeout=5)
    assert result["status"] == "pending"
    assignment = client.get(f"/internal/servers/{slot.server_id}/assignment", headers=server_header(slot)).json()
    assert assignment["runId"] == result["runId"]


def test_ack_at_assignment_deadline_keeps_the_run_and_returns_grant(backend, monkeypatch):
    client, settings, slot = backend
    service = client.app.state.use_cases.runs
    player = player_for(client)
    client.post(f"/internal/servers/{slot.server_id}/ready", headers=server_header(slot),
                json={"contentReleaseId": "sha256-test-release", "protocolVersion": 3})
    original = service._expire_assignment

    def acknowledge_at_deadline(run_id, account_id):
        current = service.server_assignment(slot.server_id, slot.secret)
        service.acknowledge(slot.server_id, slot.secret, Assignment(run_id, current["generation"]))
        return original(run_id, account_id)

    monkeypatch.setattr(service, "assignment_timeout", 0)
    monkeypatch.setattr(service, "_expire_assignment", acknowledge_at_deadline)
    response = service.assign(UUID(player["accountId"]))
    assert response["grant"]
    with psycopg.connect(settings.database_url) as connection:
        assert connection.execute("SELECT status,current_run_id FROM game_servers WHERE id=%s", (slot.server_id,)).fetchone() == (
            "occupied", UUID(response["runId"]))
        assert connection.execute("SELECT status FROM runs WHERE id=%s", (response["runId"],)).fetchone()[0] == "ready"


def test_waiting_for_assignment_does_not_keep_the_only_pool_connection(backend):
    _, settings, slot = backend
    with TestClient(game_app(replace(settings, pool_size=1))) as client:
        player = player_for(client)
        pending = assigned_run(client, slot, player)
        # The server's assignment poll and ACK need the same single connection as the waiter.
        assert client.app.state.use_cases.runs.renew_grant(UUID(pending["runId"]), UUID(player["accountId"]))["grant"]
        assert client.app.state.database.engine.pool.checkedout() == 0


def test_unconfirmed_assignment_releases_server_for_the_next_attempt(backend, monkeypatch):
    client, settings, slot = backend
    player = player_for(client)
    client.post(f"/internal/servers/{slot.server_id}/ready", headers=server_header(slot),
                json={"contentReleaseId": "sha256-test-release", "protocolVersion": 3})
    monkeypatch.setattr(client.app.state.use_cases.runs, "assignment_timeout", 0)
    with pytest.raises(ApplicationError) as failure:
        client.app.state.use_cases.runs.assign(UUID(player["accountId"]))
    assert failure.value.status_code == 503
    with psycopg.connect(settings.database_url) as connection:
        assert connection.execute("SELECT status,current_run_id FROM game_servers WHERE id=%s", (slot.server_id,)).fetchone() == ("ready", None)
        assert connection.execute("SELECT status FROM runs WHERE account_id=%s", (player["accountId"],)).fetchone()[0] == "failed"
    retry = client.app.state.use_cases.runs.assign(UUID(player["accountId"]), respond_async=True)
    assert retry["status"] == "pending"


def test_failed_run_result_commit_rolls_back_all_rewards_and_can_be_retried(backend):
    client, settings, slot = backend
    player = player_for(client)
    account_id = UUID(player["accountId"])
    pending = assigned_run(client, slot, player)
    profile = client.app.state.use_cases.profiles.get(account_id)
    endpoint = f"/internal/servers/{slot.server_id}/results"
    with psycopg.connect(settings.database_url) as connection:
        connection.execute(sql.SQL("ALTER TABLE run_result_operations ADD CONSTRAINT injected_run_failure "
                                   "CHECK (run_id <> {}::uuid)").format(sql.Literal(pending["runId"])))
    try:
        with TestClient(game_app(settings), raise_server_exceptions=False) as failing:
            response = failing.post(endpoint, headers=server_header(slot), json=result_body(pending))
            assert response.status_code == 500
    finally:
        with psycopg.connect(settings.database_url) as connection:
            connection.execute("ALTER TABLE run_result_operations DROP CONSTRAINT injected_run_failure")
    assert client.app.state.use_cases.profiles.get(account_id) == profile
    with psycopg.connect(settings.database_url) as connection:
        assert connection.execute("SELECT status FROM runs WHERE id=%s", (pending["runId"],)).fetchone()[0] == "ready"
        assert connection.execute("SELECT status FROM game_servers WHERE id=%s", (slot.server_id,)).fetchone()[0] == "occupied"
        assert connection.execute("SELECT count(*) FROM run_result_operations WHERE run_id=%s", (pending["runId"],)).fetchone()[0] == 0
    assert client.post(endpoint, headers=server_header(slot), json=result_body(pending)).status_code == 200


def test_profile_and_inventory_use_cases_work_without_http(backend):
    client, _, _ = backend
    services = client.app.state.use_cases
    account_id = UUID(player_for(client)["accountId"])
    profile = services.profiles.rename(account_id, 1, "Direct use case")
    assert profile["version"] == 2
    assert services.weapons.get()[0]["Id"]


def test_game_routes_authenticate_and_foreign_routes_are_not_registered(backend):
    client, _, _ = backend
    for headers in [{}, {"Authorization": "Bearer arbitrary-token"}]:
        assert client.get("/v1/profile", headers=headers).status_code == 401
        assert client.get("/v1/weapons", headers=headers).status_code == 404
        assert client.get("/v1/shop", headers=headers).status_code == 404
        assert client.post("/v1/runs", headers=headers, json={"requestKey": "00000000-0000-0000-0000-000000000001"}).status_code == 401
    paths = client.get("/openapi.json").json()["paths"]
    assert "/v1/guests" not in paths
    assert not any(path.startswith("/v1/auth/") for path in paths)
    profile = client.get("/v1/profile", headers={"Authorization": "Bearer test-token"})
    assert profile.status_code == 200
    assert "permanentCurrency" not in profile.json()


def test_start_request_key_returns_original_run_after_completion(backend):
    from uuid import uuid4
    client,_,slot=backend
    client.post(f'/internal/servers/{slot.server_id}/ready',headers=server_header(slot),
                json={'contentReleaseId':'sha256-test-release','protocolVersion':3})
    key=str(uuid4())
    headers={'Authorization':'Bearer test-token'}
    first=client.post('/v1/runs',headers=headers,json={'requestKey':key})
    assert first.status_code==202
    second=client.post('/v1/runs',headers=headers,json={'requestKey':key})
    assert first.json()==second.json()
    assignment=client.get(f'/internal/servers/{slot.server_id}/assignment',headers=server_header(slot)).json()
    ack={'runId':assignment['runId'],'generation':assignment['generation']}
    assert client.post(f'/internal/servers/{slot.server_id}/ack',headers=server_header(slot),json=ack).status_code==200
    assert client.post(f'/internal/servers/{slot.server_id}/ack',headers=server_header(slot),json=ack).json()['replayed']
    assert client.post(f'/internal/servers/{slot.server_id}/results',headers=server_header(slot),json=result_body(assignment)).status_code==200
    replay=client.post('/v1/runs',headers=headers,json={'requestKey':key}).json()
    assert replay['runId']==first.json()['runId']
    assert replay['status']=='completed'
    assert replay['rewardStatus']=='PENDING'


def test_checkpoint_recovery_preserves_pinned_run_and_rejects_stale_generation(backend):
    import hashlib,json
    from datetime import datetime,timedelta,timezone
    from vatican_backend.persistence.postgres.tables import GameServer
    client,_,slot=backend
    pending=assigned_run(client,slot,player_for(client))
    snapshot={'schemaVersion':4,'runId':pending['runId'],'simulationTick':100,
              'contentReleaseId':pending['contentReleaseId']}
    raw=json.dumps(snapshot)
    body={'runId':pending['runId'],'generation':pending['generation'],'schemaVersion':4,
          'simulationTick':100,'snapshotJson':raw,'sha256':hashlib.sha256(raw.encode()).hexdigest()}
    endpoint=f'/internal/servers/{slot.server_id}/checkpoint'
    assert client.post(endpoint,headers=server_header(slot),json=body).status_code==200
    assert client.post(endpoint,headers=server_header(slot),json=body).json()['replayed']
    other=json.dumps({**snapshot,'changed':True})
    assert client.post(endpoint,headers=server_header(slot),json={**body,'snapshotJson':other,'sha256':hashlib.sha256(other.encode()).hexdigest()}).status_code==409
    with client.app.state.database.transaction() as session:
        session.get(GameServer,slot.server_id).last_seen=datetime.now(timezone.utc)-timedelta(seconds=20)
    recovered=client.post(f'/internal/servers/{slot.server_id}/recover',headers=server_header(slot),
        json={'contentReleaseId':'sha256-test-release','protocolVersion':3}).json()
    assert recovered['runId']==pending['runId']
    assert recovered['generation']==pending['generation']+1
    assert recovered['weaponsJson']==pending['weaponsJson']
    assert recovered['snapshotJson']==raw
    assert client.post(f'/internal/servers/{slot.server_id}/results',headers=server_header(slot),json=result_body(pending)).status_code==409
    assert client.post(f'/internal/servers/{slot.server_id}/ack',headers=server_header(slot),
        json={'runId':recovered['runId'],'generation':recovered['generation']}).status_code==200
    assert client.post(f'/internal/servers/{slot.server_id}/results',headers=server_header(slot),json=result_body(recovered)).status_code==200
