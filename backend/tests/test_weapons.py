from uuid import uuid4

import psycopg

from test_api import backend, assigned_run, bearer, result_body, server_header


def test_new_account_has_only_sword_and_server_media(backend):
    Client, Settings, Slot = backend
    Guest = Client.post("/v1/guests").json()
    Response = Client.get("/v1/weapons", headers=bearer(Guest["token"]))
    assert Response.status_code == 200
    Inventory = Response.json()
    assert len(Inventory["Weapons"]) == 1
    assert Inventory["Weapons"][0]["ConfigurationId"] == "sword.common"
    assert Inventory["Loadout"] == [Inventory["Weapons"][0]["InstanceId"]]
    assert {Entry["Rarity"] for Entry in Inventory["Configurations"]} == {0, 1, 2, 3}
    Image = Client.get(Inventory["Configurations"][0]["IconUrl"])
    assert Image.status_code == 200
    assert Image.headers["content-type"] == "image/png"
    assert Image.content.startswith(b"\x89PNG")
    assert Client.get("/v1/weapons").status_code == 401


def test_loadout_validates_ownership_count_and_pins_run(backend):
    Client, Settings, Slot = backend
    Guest = Client.post("/v1/guests").json()
    Other = Client.post("/v1/guests").json()
    Headers = bearer(Guest["token"])
    Inventory = Client.get("/v1/weapons", headers=Headers).json()
    Sword = Inventory["Loadout"][0]
    Foreign = Client.get("/v1/weapons", headers=bearer(Other["token"])).json()["Loadout"][0]
    assert Client.put("/v1/weapons/loadout", headers=Headers, json={"InstanceIds": []}).status_code == 422
    assert Client.put("/v1/weapons/loadout", headers=Headers, json={"InstanceIds": [Sword] * 2}).status_code == 422
    assert Client.put("/v1/weapons/loadout", headers=Headers, json={"InstanceIds": [Foreign]}).status_code == 403
    Additional = [uuid4(), uuid4(), uuid4()]
    with psycopg.connect(Settings.database_url) as Database:
        for Index, InstanceId in enumerate(Additional):
            Database.execute("INSERT INTO owned_weapons(instance_id,account_id,configuration_id,grant_key) VALUES (%s,%s,%s,%s)",
                             (InstanceId, Guest["accountId"], "spear.rare", "fixture:" + str(Index)))
    Selection = [Sword, str(Additional[0]), str(Additional[1])]
    assert Client.put("/v1/weapons/loadout", headers=Headers, json={"InstanceIds": Selection + [str(Additional[2])]}).status_code == 422
    assert Client.put("/v1/weapons/loadout", headers=Headers, json={"InstanceIds": Selection}).status_code == 200
    Pending = assigned_run(Client, Slot, Guest)
    import json
    Pinned = json.loads(Pending["weaponsJson"])
    assert Pinned["Loadout"] == Selection
    assert len(Pinned["Weapons"]) == 3
    assert Client.put("/v1/weapons/loadout", headers=Headers, json={"InstanceIds": Selection}).status_code == 200
    assert Client.put("/v1/weapons/loadout", headers=Headers, json={"InstanceIds": [Sword]}).status_code == 409


def test_match_weapon_and_progression_are_transactional_and_idempotent(backend):
    Client, Settings, Slot = backend
    Guest = Client.post("/v1/guests").json()
    Headers = bearer(Guest["token"])
    Before = Client.get("/v1/weapons", headers=Headers).json()
    Pending = assigned_run(Client, Slot, Guest)
    Body = result_body(Pending)
    Endpoint = f"/internal/servers/{Slot.server_id}/results"
    First = Client.post(Endpoint, headers=server_header(Slot), json=Body)
    assert First.status_code == 200
    After = Client.get("/v1/weapons", headers=Headers).json()
    assert len(After["Weapons"]) == len(Before["Weapons"]) + 1
    Starter = next(Entry for Entry in After["Weapons"] if Entry["InstanceId"] == Before["Weapons"][0]["InstanceId"])
    assert Starter["Level"] == 2
    assert Starter["Experience"] == 100
    Repeated = Client.post(Endpoint, headers=server_header(Slot), json=Body)
    assert Repeated.status_code == 200 and Repeated.json()["replayed"]
    Final = Client.get("/v1/weapons", headers=Headers).json()
    assert Final["Weapons"] == After["Weapons"]
