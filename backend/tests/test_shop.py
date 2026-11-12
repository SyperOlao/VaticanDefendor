import json
from concurrent.futures import ThreadPoolExecutor
from copy import deepcopy
from dataclasses import replace
from pathlib import Path
from uuid import uuid4

import psycopg
from psycopg import sql
from psycopg.conninfo import make_conninfo
import pytest
from fastapi.testclient import TestClient

from test_api import backend, assigned_run, bearer, result_body, server_header
from vatican_backend.api import create_app
from vatican_backend.migrate import migrate
from vatican_backend.shop import build_catalog, validate_catalog
from vatican_backend.weapons import LoadCatalog


CATALOG = {"version": "test-v1", "offers": [
    {"id": "repeat", "weaponConfigurationId": "spear.rare", "title": "Копьё", "price": 150, "purchaseLimit": 0},
    {"id": "limited", "weaponConfigurationId": "spear.rare", "title": "Лимит", "price": 10, "purchaseLimit": 1},
    {"id": "disabled", "weaponConfigurationId": "axe.rare", "title": "Недоступно", "price": 10,
     "purchaseLimit": 0, "available": False},
    {"id": "free", "weaponConfigurationId": "sword.rare", "title": "Подарок", "price": 0, "purchaseLimit": 1}]}


@pytest.fixture()
def shop_backend(backend):
    _, settings, slot = backend
    settings = replace(settings, shop_catalog=deepcopy(CATALOG))
    with TestClient(create_app(settings)) as client:
        yield client, settings, slot


def player(client, settings, balance=500):
    guest = client.post("/v1/guests").json()
    with psycopg.connect(settings.database_url) as db:
        db.execute("UPDATE profiles SET permanent_currency=%s WHERE account_id=%s", (balance, guest["accountId"]))
    return guest, bearer(guest["token"])


def intention(client, headers, offer="repeat"):
    return {"operationId": str(uuid4()), "offerId": offer,
            "catalogVersion": client.get("/v1/shop", headers=headers).json()["version"]}


def test_catalog_and_purchase_unique_instances_without_changing_loadout(shop_backend):
    client, settings, _ = shop_backend
    _, headers = player(client, settings)
    before = client.get("/v1/weapons", headers=headers).json()
    shop = client.get("/v1/shop", headers=headers).json()
    assert shop["currencyId"] == "prestige" and shop["walletBalance"] == 500
    assert all(offer["purchasedCount"] == 0 for offer in shop["offers"])
    assert {o["weaponConfigurationId"] for o in shop["offers"]} == {c["Id"] for c in shop["configurations"]}
    assert all("Damage" in c and "SpecialAbility" in c and "MaximumLevel" in c for c in shop["configurations"])
    receipts = []
    for index in range(2):
        body = intention(client, headers)
        response = client.post("/v1/purchases", headers=headers, json=body)
        assert response.status_code == 200, response.text
        receipt = response.json()
        assert receipt == {"operationId": body["operationId"], "status": "Completed", "offerId": "repeat",
                           "chargedAmount": 150, "currencyId": "prestige", "walletBalance": 500 - 150 * (index + 1),
                           "walletRevision": shop["walletRevision"] + index + 1,
                           "inventoryRevision": before["Revision"] + index + 1,
                           "grantedWeaponInstanceIds": receipt["grantedWeaponInstanceIds"], "replayed": False}
        assert len(receipt["grantedWeaponInstanceIds"]) == 1
        receipts.append(receipt)
    after = client.get("/v1/weapons", headers=headers).json()
    assert after["Loadout"] == before["Loadout"]
    assert len(after["Weapons"]) == 3 and after["Revision"] >= receipts[-1]["inventoryRevision"]
    ids = [r["grantedWeaponInstanceIds"][0] for r in receipts]
    assert len(set(ids)) == 2
    assert all(w["ConfigurationId"] == "spear.rare" and w["Level"] == 1 and w["Experience"] == 0
               for w in after["Weapons"] if w["InstanceId"] in ids)
    assert client.get("/v1/profile", headers=headers).json()["permanentCurrency"] == 200
    assert client.get("/v1/inventory", headers=headers).json() == {"items": []}
    updated = client.get("/v1/shop", headers=headers).json()
    assert updated["walletBalance"] == 200 and updated["version"] == shop["version"]
    assert next(o for o in updated["offers"] if o["id"] == "repeat")["purchasedCount"] == 2


def test_concurrent_replays_and_restart_return_one_durable_result(shop_backend):
    client, settings, _ = shop_backend
    _, headers = player(client, settings)
    before = client.get("/v1/weapons", headers=headers).json()
    body = intention(client, headers)
    with ThreadPoolExecutor(max_workers=8) as executor:
        replies = list(executor.map(lambda _: client.post("/v1/purchases", headers=headers, json=body), range(8)))
    assert all(r.status_code == 200 for r in replies)
    assert sum(not r.json()["replayed"] for r in replies) == 1
    receipt = next(r.json() for r in replies if not r.json()["replayed"])
    assert all(r.json() == {**receipt, "replayed": r.json()["replayed"]} for r in replies)
    # A deployment changes both price and snapshot version; replay must run before catalog checks.
    changed = deepcopy(CATALOG)
    changed["offers"][0]["price"] = 999
    with TestClient(create_app(replace(settings, shop_catalog=changed))) as restarted:
        assert restarted.get("/v1/shop", headers=headers).json()["version"] != body["catalogVersion"]
        assert restarted.post("/v1/purchases", headers=headers, json=body).json() == {**receipt, "replayed": True}
        assert restarted.get("/v1/purchases/" + body["operationId"], headers=headers).json() == {**receipt, "replayed": True}
    after = client.get("/v1/weapons", headers=headers).json()
    assert len(after["Weapons"]) == len(before["Weapons"]) + 1
    assert client.get("/v1/profile", headers=headers).json()["permanentCurrency"] == 350


@pytest.mark.parametrize("offer,balance,code", [
    ("repeat", 149, "InsufficientFunds"), ("disabled", 500, "OfferUnavailable"),
    ("missing", 500, "OfferUnavailable"), ("stale", 500, "CatalogChanged")])
def test_rejections_are_final_durable_and_leave_state_unchanged(shop_backend, offer, balance, code):
    client, settings, _ = shop_backend
    guest, headers = player(client, settings, balance)
    before = client.get("/v1/weapons", headers=headers).json()
    shop = client.get("/v1/shop", headers=headers).json()
    body = intention(client, headers, offer if offer != "stale" else "repeat")
    if offer == "stale":
        body["catalogVersion"] = "old-version"
    response = client.post("/v1/purchases", headers=headers, json=body)
    assert response.status_code == 409
    expected = {"operationId": body["operationId"], "status": "Rejected", "offerId": body["offerId"],
                "code": code, "replayed": False}
    assert response.json() == expected
    assert client.get("/v1/weapons", headers=headers).json() == before
    assert client.get("/v1/shop", headers=headers).json() == shop
    # Later funds and a restart cannot turn the same rejected operation into a purchase.
    with psycopg.connect(settings.database_url) as db:
        db.execute("UPDATE profiles SET permanent_currency=1000,wallet_revision=wallet_revision+1 WHERE account_id=%s",
                   (guest["accountId"],))
    with TestClient(create_app(settings)) as restarted:
        replay = restarted.post("/v1/purchases", headers=headers, json=body)
        assert replay.status_code == 409 and replay.json() == {**expected, "replayed": True}
        lookup = restarted.get("/v1/purchases/" + body["operationId"], headers=headers)
        assert lookup.status_code == 200 and lookup.json() == {**expected, "replayed": True}


@pytest.mark.parametrize("offer,balance,expected_balance,code", [
    ("repeat", 150, 0, "InsufficientFunds"), ("limited", 500, 490, "PurchaseLimitReached")])
def test_different_devices_cannot_overspend_or_exceed_limit(shop_backend, offer, balance, expected_balance, code):
    client, settings, _ = shop_backend
    _, headers = player(client, settings, balance)
    bodies = [intention(client, headers, offer) for _ in range(8)]
    with ThreadPoolExecutor(max_workers=8) as executor:
        replies = list(executor.map(lambda body: client.post("/v1/purchases", headers=headers, json=body), bodies))
    assert sum(r.status_code == 200 for r in replies) == 1
    assert all(r.json()["code"] == code for r in replies if r.status_code != 200)
    inventory = client.get("/v1/weapons", headers=headers).json()
    assert len(inventory["Weapons"]) == 2
    shop = client.get("/v1/shop", headers=headers).json()
    assert shop["walletBalance"] == expected_balance
    entry = next(o for o in shop["offers"] if o["id"] == offer)
    assert entry["purchasedCount"] == 1
    if offer == "limited":
        assert not entry["available"] and entry["unavailableReason"] == code


def test_operation_conflicts_and_account_isolation(shop_backend):
    client, settings, _ = shop_backend
    _, headers = player(client, settings)
    _, other_headers = player(client, settings)
    body = intention(client, headers)
    first = client.post("/v1/purchases", headers=headers, json=body).json()
    for changed in ({"offerId": "limited"}, {"catalogVersion": "different"}):
        response = client.post("/v1/purchases", headers=headers, json={**body, **changed})
        assert response.status_code == 409 and response.json()["code"] == "OperationConflict"
        assert "status" not in response.json()
    path = "/v1/purchases/" + body["operationId"]
    assert client.get(path, headers=headers).json() == {**first, "replayed": True}
    missing = client.get(path, headers=other_headers)
    assert missing.status_code == 404 and missing.json()["code"] == "OperationNotFound"
    # The UUID is scoped to the account, not globally consumed by its first owner.
    second = client.post("/v1/purchases", headers=other_headers, json=body).json()
    assert second["status"] == "Completed" and not second["replayed"]
    assert second["grantedWeaponInstanceIds"] != first["grantedWeaponInstanceIds"]


@pytest.mark.parametrize("change", [
    {"price": 0}, {"walletBalance": 9999}, {"accountId": str(uuid4())}, {"quantity": 2},
    {"weaponConfigurationId": "sword.legendary"}, {"operationId": "invalid"}, {"offerId": ""},
    {"catalogVersion": ""}, {"catalogVersion": 1}])
def test_invalid_requests_do_not_consume_operation(shop_backend, change):
    client, settings, _ = shop_backend
    _, headers = player(client, settings)
    body = intention(client, headers)
    invalid = client.post("/v1/purchases", headers=headers, json={**body, **change})
    assert invalid.status_code == 400 and invalid.json()["code"] == "InvalidRequest"
    assert client.get("/v1/purchases/" + body["operationId"], headers=headers).status_code == 404
    assert client.post("/v1/purchases", headers=headers, json=body).status_code == 200


def test_unauthorized_malformed_and_empty_catalog(backend):
    client, settings, _ = backend
    guest, headers = player(client, settings, 0)
    for path in ("/v1/shop", "/v1/purchases/" + str(uuid4())):
        assert client.get(path).status_code == 401
        assert client.get(path).json()["code"] == "Unauthorized"
    body = intention(client, headers)
    assert client.post("/v1/purchases", json=body).json()["code"] == "Unauthorized"
    malformed = client.post("/v1/purchases", headers={**headers, "Content-Type": "application/json"}, content="{")
    assert malformed.status_code == 400 and malformed.json()["code"] == "InvalidRequest"
    assert client.get("/v1/purchases/not-a-uuid", headers=headers).status_code == 400
    shop = client.get("/v1/shop", headers=headers).json()
    assert shop["offers"] == [] and shop["configurations"] == [] and shop["walletBalance"] == 0


def test_registered_access_token_can_purchase_and_resume_after_login(shop_backend):
    client, _, _ = shop_backend
    credentials = {"login": "shop_registered", "password": "secret123"}
    session = client.post("/v1/auth/register", json=credentials).json()
    headers = bearer(session["accessToken"])
    body = intention(client, headers, "free")
    receipt = client.post("/v1/purchases", headers=headers, json=body).json()
    assert receipt["status"] == "Completed"
    client.post("/v1/auth/logout", headers=headers)
    unauthorized = client.post("/v1/purchases", headers=headers, json=body)
    assert unauthorized.status_code == 401 and unauthorized.json()["code"] == "Unauthorized"
    logged_in = client.post("/v1/auth/login", json=credentials).json()
    assert client.post("/v1/purchases", headers=bearer(logged_in["accessToken"]), json=body).json() == {
        **receipt, "replayed": True}


def test_free_purchase_and_independent_revisions_and_match_rewards(shop_backend):
    client, settings, slot = shop_backend
    guest, headers = player(client, settings, 0)
    inventory = client.get("/v1/weapons", headers=headers).json()
    shop = client.get("/v1/shop", headers=headers).json()
    profile = client.get("/v1/profile", headers=headers).json()
    client.patch("/v1/profile", headers=headers, json={"expectedVersion": profile["version"], "displayName": "Name"})
    assert client.get("/v1/shop", headers=headers).json() == shop
    assert client.get("/v1/weapons", headers=headers).json() == inventory
    receipt = client.post("/v1/purchases", headers=headers, json=intention(client, headers, "free")).json()
    assert receipt["chargedAmount"] == 0 and receipt["walletBalance"] == 0
    assert receipt["walletRevision"] == shop["walletRevision"] + 1
    selected = client.put("/v1/weapons/loadout", headers=headers,
                          json={"InstanceIds": receipt["grantedWeaponInstanceIds"]}).json()
    assert selected["Revision"] == receipt["inventoryRevision"] + 1
    assert client.get("/v1/shop", headers=headers).json()["walletRevision"] == receipt["walletRevision"]
    run = assigned_run(client, slot, guest)
    result = client.post(f"/internal/servers/{slot.server_id}/results", headers=server_header(slot), json=result_body(run))
    assert result.status_code == 200
    after = client.get("/v1/shop", headers=headers).json()
    assert after["walletBalance"] == 25 and after["walletRevision"] == receipt["walletRevision"] + 1
    assert client.get("/v1/weapons", headers=headers).json()["Revision"] == selected["Revision"] + 1


def test_purchase_during_run_preserves_snapshot_and_is_selectable_next_run(shop_backend):
    client, settings, slot = shop_backend
    guest, headers = player(client, settings)
    run = assigned_run(client, slot, guest)
    pinned = json.loads(run["weaponsJson"])
    receipt = client.post("/v1/purchases", headers=headers, json=intention(client, headers)).json()
    with psycopg.connect(settings.database_url) as db:
        assert db.execute("SELECT weapon_snapshot FROM runs WHERE id=%s", (run["runId"],)).fetchone()[0] == pinned
    inventory = client.get("/v1/weapons", headers=headers).json()
    assert inventory["Loadout"] == pinned["Loadout"]
    assert receipt["grantedWeaponInstanceIds"][0] in [w["InstanceId"] for w in inventory["Weapons"]]
    client.post(f"/internal/servers/{slot.server_id}/results", headers=server_header(slot), json=result_body(run))
    assert client.put("/v1/weapons/loadout", headers=headers,
                      json={"InstanceIds": receipt["grantedWeaponInstanceIds"]}).status_code == 200
    next_run = assigned_run(client, slot, guest)
    assert json.loads(next_run["weaponsJson"])["Loadout"] == receipt["grantedWeaponInstanceIds"]


def test_failure_to_save_result_rolls_back_charge_grant_and_revisions(shop_backend):
    client, settings, _ = shop_backend
    _, headers = player(client, settings)
    before = client.get("/v1/weapons", headers=headers).json()
    shop = client.get("/v1/shop", headers=headers).json()
    body = intention(client, headers)
    with psycopg.connect(settings.database_url) as db:
        db.execute(sql.SQL("ALTER TABLE purchase_operations ADD CONSTRAINT injected_failure "
                           "CHECK (operation_id <> {}::uuid)").format(sql.Literal(body["operationId"])))
    try:
        with TestClient(create_app(settings), raise_server_exceptions=False) as failing:
            response = failing.post("/v1/purchases", headers=headers, json=body)
            assert response.status_code == 500 and response.json()["code"] == "InternalError"
    finally:
        with psycopg.connect(settings.database_url) as db:
            db.execute("ALTER TABLE purchase_operations DROP CONSTRAINT injected_failure")
    assert client.get("/v1/weapons", headers=headers).json() == before
    assert client.get("/v1/shop", headers=headers).json() == shop
    assert client.get("/v1/purchases/" + body["operationId"], headers=headers).status_code == 404
    assert client.post("/v1/purchases", headers=headers, json=body).status_code == 200


def test_definitions_are_immutable_and_removed_owned_definitions_remain_resolvable(shop_backend, monkeypatch):
    client, settings, _ = shop_backend
    _, headers = player(client, settings)
    receipt = client.post("/v1/purchases", headers=headers, json=intention(client, headers)).json()
    definitions = LoadCatalog()
    reduced = [c for c in definitions if c["Id"] != "spear.rare"]
    monkeypatch.setattr("vatican_backend.weapons.LoadCatalog", lambda: reduced)
    with TestClient(create_app(replace(settings, shop_catalog={}))) as restarted:
        inventory = restarted.get("/v1/weapons", headers=headers).json()
        assert next(c for c in inventory["Configurations"] if c["Id"] == "spear.rare") == next(c for c in definitions if c["Id"] == "spear.rare")
        assert receipt["grantedWeaponInstanceIds"][0] in [w["InstanceId"] for w in inventory["Weapons"]]
        assert restarted.get("/v1/weapons/media/spear.rare.png").status_code == 200
    modified = deepcopy(definitions)
    modified[0]["Damage"] += 1
    monkeypatch.setattr("vatican_backend.weapons.LoadCatalog", lambda: modified)
    with pytest.raises(ValueError, match="immutable"):
        with TestClient(create_app(settings)):
            pass


def test_catalog_versions_cover_price_configuration_and_parameters():
    weapons = LoadCatalog()
    initial = build_catalog(CATALOG, weapons)["version"]
    for key, value in (("price", 151), ("weaponConfigurationId", "axe.rare"), ("purchaseLimit", 1), ("available", False)):
        changed = deepcopy(CATALOG)
        changed["offers"][0][key] = value
        assert build_catalog(changed, weapons)["version"] != initial
    changed_weapons = deepcopy(weapons)
    next(c for c in changed_weapons if c["Id"] == "spear.rare")["Damage"] += 1
    assert build_catalog(CATALOG, changed_weapons)["version"] != initial
    invalid = deepcopy(CATALOG)
    invalid["offers"][0]["weaponConfigurationId"] = "unknown"
    with pytest.raises(ValueError, match="unknown"):
        build_catalog(invalid, weapons)


@pytest.mark.parametrize("change", [{"price": -1}, {"price": True}, {"price": 1.5}, {"price": 2**63},
                                         {"purchaseLimit": -1}, {"available": 1}, {"currencyId": "wood"},
                                         {"quantity": 1}, {"purchasedCount": 0}])
def test_catalog_rejects_invalid_authoritative_data(change):
    catalog = deepcopy(CATALOG)
    catalog["offers"][0].update(change)
    with pytest.raises(ValueError):
        validate_catalog(catalog)


def test_migration_preserves_existing_balance_inventory_revisions_and_operation_ids(backend):
    _, settings, _ = backend
    schema = "shop_upgrade_" + uuid4().hex
    url = make_conninfo(settings.database_url, options="-c search_path=" + schema)
    account_id, instance_id, operation_id = uuid4(), uuid4(), uuid4()
    legacy = {"operationId": str(operation_id), "offerId": "legacy.offer", "itemId": "legacy.item",
              "catalogVersion": "old", "quantity": 1, "charged": 10, "permanentCurrency": 490, "profileVersion": 9}
    with psycopg.connect(settings.database_url) as db:
        db.execute(sql.SQL("CREATE SCHEMA {}").format(sql.Identifier(schema)))
    try:
        with psycopg.connect(url) as db:
            db.execute("CREATE TABLE schema_migrations(version varchar(64) PRIMARY KEY)")
            for path in sorted((Path(__file__).parents[1] / "migrations").glob("*.sql")):
                if path.name >= "008":
                    break
                db.execute(path.read_text())
                db.execute("INSERT INTO schema_migrations VALUES (%s)", (path.stem,))
            db.execute("INSERT INTO accounts(id) VALUES (%s)", (account_id,))
            db.execute("INSERT INTO profiles(account_id,version,display_name,permanent_currency) VALUES (%s,9,'Old',490)", (account_id,))
            db.execute("INSERT INTO owned_weapons(instance_id,account_id,configuration_id,grant_key) "
                       "VALUES (%s,%s,'sword.common','old.grant')", (instance_id, account_id))
            db.execute("INSERT INTO purchase_operations(account_id,operation_id,offer_id,catalog_version,receipt_json) "
                       "VALUES (%s,%s,'legacy.offer','old',%s::jsonb)", (account_id, operation_id, json.dumps(legacy)))
        migrate(url)
        migrate(url)  # Reapplying must preserve state and revision floors.
        with psycopg.connect(url) as db:
            assert db.execute("SELECT permanent_currency,wallet_revision,inventory_revision FROM profiles").fetchone() == (490, 9, 9)
            assert db.execute("SELECT instance_id FROM owned_weapons").fetchone()[0] == instance_id
            assert db.execute("SELECT receipt_json FROM purchase_operations").fetchone()[0] == legacy
    finally:
        with psycopg.connect(settings.database_url) as db:
            db.execute(sql.SQL("DROP SCHEMA {} CASCADE").format(sql.Identifier(schema)))
