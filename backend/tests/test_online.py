import hashlib
import json
from concurrent.futures import ThreadPoolExecutor
from dataclasses import replace
from uuid import uuid4

import psycopg
import pytest
from fastapi.testclient import TestClient
from test_api import backend, bearer, assigned_run, server_header, result_body
from vatican_backend.api import create_app
from vatican_backend.settings import Settings
from vatican_backend.shop import validate_catalog

CATALOG = {"version": "test-v1", "offers": [
    {"id": "test.offer", "itemId": "test.item", "title": "Test only", "price": 10, "quantity": 1, "maxOwned": 2},
    {"id": "test.other", "itemId": "test.other", "title": "Test only", "price": 20, "quantity": 1, "maxOwned": 1}]}


def test_six_character_password_registration_login_and_recovery(backend):
    client, _, _ = backend
    credentials = {"login": "short_password", "password": "abc123"}
    assert client.post("/v1/auth/register", json={**credentials, "password": "abc12"}).status_code == 422
    registered = client.post("/v1/auth/register", json=credentials)
    assert registered.status_code == 201
    session = registered.json()
    assert client.post("/v1/auth/login", json=credentials).status_code == 200
    recovery = {**credentials, "password": "xyz789", "recoveryCode": session["recoveryCode"]}
    assert client.post("/v1/auth/recover", json={**recovery, "password": "xyz78"}).status_code == 422
    assert client.post("/v1/auth/recover", json=recovery).status_code == 200
    assert client.post("/v1/auth/login", json=credentials).status_code == 401
    assert client.post("/v1/auth/login", json={**credentials, "password": "xyz789"}).status_code == 200


def test_account_link_recovery_and_revocation(backend):
    client, settings, _ = backend
    guest = client.post("/v1/guests").json()
    credentials = {"login": "Player_One", "password": "test-only-password-123"}
    response = client.post("/v1/auth/register", headers=bearer(guest["token"]), json=credentials)
    assert response.status_code == 201
    session = response.json()
    assert session["accountId"] == guest["accountId"]
    assert client.get("/v1/profile", headers=bearer(guest["token"])).status_code == 401
    assert client.get("/v1/profile", headers=bearer(session["accessToken"])).status_code == 200
    with psycopg.connect(settings.database_url) as db:
        stored = db.execute("SELECT password_hash FROM account_logins WHERE account_id=%s", (guest["accountId"],)).fetchone()[0]
        assert stored.startswith("$argon2id$") and credentials["password"] not in stored
    assert client.post("/v1/auth/register", json={**credentials, "login": "player_one"}).status_code == 409
    assert client.post("/v1/auth/login", json={**credentials, "password": "wrong-password-123"}).status_code == 401
    logged = client.post("/v1/auth/login", json={**credentials, "login": "player_one"}).json()
    recovery = {"login": "player_one", "password": "new-password-1234", "recoveryCode": session["recoveryCode"]}
    recovered = client.post("/v1/auth/recover", json=recovery)
    assert recovered.status_code == 200 and recovered.json()["accountId"] == guest["accountId"]
    assert recovered.json()["recoveryCode"] != session["recoveryCode"]
    assert client.get("/v1/profile", headers=bearer(logged["accessToken"])).status_code == 401
    assert client.post("/v1/auth/refresh", json={"refreshToken": logged["refreshToken"]}).status_code == 401
    assert client.post("/v1/auth/recover", json=recovery).status_code == 401


def test_concurrent_refresh_and_logout(backend):
    client, _, _ = backend
    session = client.post("/v1/auth/register", json={"login": "rotate", "password": "test-password-1234"}).json()
    with ThreadPoolExecutor(max_workers=2) as executor:
        results = list(executor.map(lambda _: client.post("/v1/auth/refresh", json={"refreshToken": session["refreshToken"]}), range(2)))
    assert sorted(r.status_code for r in results) == [200, 401]
    current = next(r.json() for r in results if r.status_code == 200)
    assert client.get("/v1/profile", headers=bearer(session["accessToken"])).status_code == 401
    assert client.post("/v1/auth/logout", headers=bearer(current["accessToken"])).status_code == 204
    assert client.get("/v1/profile", headers=bearer(current["accessToken"])).status_code == 401


def test_auth_rate_limit(backend):
    client, _, _ = backend
    for _ in range(10):
        assert client.post("/v1/auth/login", json={"login": "missing", "password": "test-password-1234"}).status_code == 401
    assert client.post("/v1/auth/login", json={"login": "missing", "password": "test-password-1234"}).status_code == 429


def test_purchase_replay_conflict_and_authority(backend):
    _, settings, slot = backend
    with TestClient(create_app(replace(settings, shop_catalog=CATALOG))) as client:
        guest = client.post("/v1/guests").json()
        run = assigned_run(client, slot, guest)
        assert client.post(f"/internal/servers/{slot.server_id}/results", headers=server_header(slot), json=result_body(run)).status_code == 200
        headers = bearer(guest["token"])
        purchase = {"operationId": str(uuid4()), "offerId": "test.offer", "catalogVersion": "test-v1"}
        assert client.post("/v1/purchases", headers=headers, json={**purchase, "price": 0}).status_code == 422
        with ThreadPoolExecutor(max_workers=4) as executor:
            responses = list(executor.map(lambda _: client.post("/v1/purchases", headers=headers, json=purchase), range(4)))
        assert all(r.status_code == 200 for r in responses)
        assert sum(not r.json()["replayed"] for r in responses) == 1
        assert client.get("/v1/profile", headers=headers).json()["permanentCurrency"] == 15
        assert client.get("/v1/inventory", headers=headers).json() == {"items": [{"itemId": "test.item", "quantity": 1}]}
        assert client.post("/v1/purchases", headers=headers, json={**purchase, "offerId": "test.other"}).status_code == 409
        assert client.post("/v1/purchases", headers=headers, json={**purchase, "operationId": str(uuid4()), "offerId": "test.other"}).status_code == 409
        stranger = client.post("/v1/guests").json()
        assert client.get("/v1/inventory", headers=bearer(stranger["token"])).json() == {"items": []}
    with TestClient(create_app(settings)) as client:
        assert client.post("/v1/purchases", headers=headers, json=purchase).json()["replayed"] is True


def test_distinct_concurrent_purchases_cannot_overspend(backend):
    _, settings, slot = backend
    with TestClient(create_app(replace(settings, shop_catalog=CATALOG))) as client:
        guest = client.post("/v1/guests").json()
        run = assigned_run(client, slot, guest)
        client.post(f"/internal/servers/{slot.server_id}/results", headers=server_header(slot), json=result_body(run))
        def buy(_):
            return client.post("/v1/purchases", headers=bearer(guest["token"]), json={"operationId": str(uuid4()),
                               "offerId": "test.other", "catalogVersion": "test-v1"})
        with ThreadPoolExecutor(max_workers=2) as executor:
            replies = list(executor.map(buy, range(2)))
        assert sorted(r.status_code for r in replies) == [200, 409]
        assert client.get("/v1/profile", headers=bearer(guest["token"])).json()["permanentCurrency"] == 5


def test_content_publication_integrity_and_immutability(backend):
    _, settings, _ = backend
    with TestClient(create_app(replace(settings, content_admin_secret="test-admin-secret" * 3))) as client:
        release_id = "sha256-" + "a" * 64
        payload = json.dumps({"_releaseId": release_id, "_contentSchemaVersion": 5})
        body = {"releaseId": release_id, "schemaVersion": 5, "payloadJson": payload,
                "payloadSha256": hashlib.sha256(payload.encode()).hexdigest()}
        path = "/internal/content/" + release_id
        headers = {"X-Content-Admin-Secret": "test-admin-secret" * 3}
        assert client.put(path, json=body).status_code == 401
        assert client.put(path, headers=headers, json={**body, "payloadSha256": "0" * 64}).status_code == 422
        assert client.put(path, headers=headers, json=body).json()["replayed"] is False
        assert client.put(path, headers=headers, json=body).json()["replayed"] is True
        downloaded = client.get("/v1/content/" + release_id)
        assert downloaded.json() == body
        assert "immutable" in downloaded.headers["cache-control"]
        payload += " "
        assert client.put(path, headers=headers, json={**body, "payloadJson": payload,
                          "payloadSha256": hashlib.sha256(payload.encode()).hexdigest()}).status_code == 409


def test_async_assignment_and_owner_only_status(backend):
    client, _, slot = backend
    guest = client.post("/v1/guests").json()
    client.post(f"/internal/servers/{slot.server_id}/ready", headers=server_header(slot),
                json={"contentReleaseId": "sha256-test-release", "protocolVersion": 3})
    headers = {**bearer(guest["token"]), "Prefer": "respond-async"}
    pending = client.post("/v1/runs", headers=headers)
    assert pending.status_code == 202
    assert pending.json()["runId"] == client.post("/v1/runs", headers=headers).json()["runId"]
    location = pending.headers["location"]
    stranger = client.post("/v1/guests").json()
    assert client.get(location, headers=bearer(stranger["token"])).status_code == 404
    assert client.get(location, headers=headers).status_code == 202
    assignment = client.get(f"/internal/servers/{slot.server_id}/assignment", headers=server_header(slot)).json()
    client.post(f"/internal/servers/{slot.server_id}/ack", headers=server_header(slot),
                json={"runId": assignment["runId"], "generation": assignment["generation"]})
    ready = client.get(location, headers=headers)
    assert ready.status_code == 200 and ready.json()["grant"]


def test_remote_transport_requires_dtls(monkeypatch):
    monkeypatch.setenv("VD_DATABASE_URL", "unused")
    slot = {"serverId": str(uuid4()), "host": "192.0.2.10", "port": 7979, "secret": "test-only" * 5}
    monkeypatch.setenv("VD_SERVER_POOL_JSON", json.dumps([slot]))
    with pytest.raises(ValueError, match="DTLS"):
        Settings.from_environment()
    slot.update(transport="dtls", certificateName="game.example.test")
    monkeypatch.setenv("VD_SERVER_POOL_JSON", json.dumps([slot]))
    assert next(iter(Settings.from_environment().servers.values())).transport == "dtls"


def test_catalog_rejects_invalid_prices_and_duplicates():
    with pytest.raises(ValueError):
        validate_catalog({**CATALOG, "offers": [{**CATALOG["offers"][0], "price": -1}]})
    with pytest.raises(ValueError):
        validate_catalog({**CATALOG, "offers": [CATALOG["offers"][0]] * 2})


def test_purchase_receipt_is_private_and_durable(backend):
    _, settings, slot = backend
    with TestClient(create_app(replace(settings, shop_catalog=CATALOG))) as client:
        guest = client.post("/v1/guests").json()
        run = assigned_run(client, slot, guest)
        client.post(f"/internal/servers/{slot.server_id}/results", headers=server_header(slot), json=result_body(run))
        op = str(uuid4())
        headers = bearer(guest["token"])
        body = {"operationId": op, "offerId": "test.offer", "catalogVersion": "obsolete"}
        assert client.post("/v1/purchases", headers=headers, json=body).status_code == 409
        assert client.post("/v1/purchases", headers=headers, json={**body, "catalogVersion": "test-v1"}).status_code == 200
        receipt = client.get("/v1/purchases/" + op, headers=headers)
        assert receipt.status_code == 200 and receipt.json()["charged"] == 10
        stranger = client.post("/v1/guests").json()
        assert client.get("/v1/purchases/" + op, headers=bearer(stranger["token"])).status_code == 404


def test_recovery_fences_racing_refresh(backend):
    client, _, _ = backend
    old = client.post("/v1/auth/register", json={"login": "recover_race", "password": "test-password-1234"}).json()
    with ThreadPoolExecutor(max_workers=2) as executor:
        refreshed = executor.submit(lambda: client.post("/v1/auth/refresh", json={"refreshToken": old["refreshToken"]}))
        recovery = executor.submit(lambda: client.post("/v1/auth/recover", json={"login": "recover_race",
                                   "password": "new-password-5678", "recoveryCode": old["recoveryCode"]}))
        fresh, recovered = refreshed.result(), recovery.result()
    assert recovered.status_code == 200
    assert fresh.status_code in (200, 401)
    if fresh.status_code == 200:
        assert client.get("/v1/profile", headers=bearer(fresh.json()["accessToken"])).status_code == 401
    assert client.get("/v1/profile", headers=bearer(recovered.json()["accessToken"])).status_code == 200


def test_catalog_file_and_source_conflict(monkeypatch, tmp_path):
    path = tmp_path / "shop.json"
    path.write_text(json.dumps(CATALOG))
    monkeypatch.setenv("VD_DATABASE_URL", "unused")
    monkeypatch.setenv("VD_SERVER_POOL_JSON", json.dumps([{"serverId": str(uuid4()), "host": "127.0.0.1", "port": 7979, "secret": "x" * 40}]))
    monkeypatch.setenv("VD_SHOP_CATALOG_FILE", str(path))
    assert Settings.from_environment().shop_catalog == CATALOG
    monkeypatch.setenv("VD_SHOP_CATALOG_JSON", "{}")
    with pytest.raises(ValueError, match="only one"):
        Settings.from_environment()
