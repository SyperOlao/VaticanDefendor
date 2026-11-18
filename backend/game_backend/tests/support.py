"""Helpers for the current ORM and use-case tests."""
from concurrent.futures import ThreadPoolExecutor
import time
from uuid import NAMESPACE_URL, UUID, uuid4, uuid5



def server_header(slot):
    return {"X-Server-Secret": slot.secret}


def assigned_run(client, slot, player):
    assert client.post(f"/internal/servers/{slot.server_id}/ready", headers=server_header(slot),
                       json={"contentReleaseId": "sha256-test-release", "protocolVersion": 3}).status_code == 200
    with ThreadPoolExecutor(max_workers=1) as pool:
        future = pool.submit(client.app.state.use_cases.runs.assign, UUID(player["accountId"]))
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
        assert future.result(timeout=5)["runId"] == pending["runId"]
    return pending


def result_body(pending, outcome="victory"):
    return {"runId": pending["runId"], "accountId": pending["accountId"],
            "contentReleaseId": pending["contentReleaseId"],
            "generation": pending["generation"], "outcome": outcome,
            "churchLevel": 1}


def game_app(settings):
    """Create an app with a known local profile for direct use-case tests."""
    from vatican_backend.api import create_app

    account_id = uuid5(NAMESPACE_URL, settings.database_url)
    app = create_app(settings, auth_gateway=FakeAuth(account_id), inventory_gateway=FakeInventory())
    app.state.test_player = {"accountId": str(account_id)}
    return app


def player_for(client):
    return client.app.state.test_player


class FakeAuth:
    def __init__(self, account_id):
        self.account_id = account_id

    def authenticate(self, token):
        from vatican_backend.application.ports import Principal
        from vatican_backend.application.errors import ApplicationError
        if token != "test-token":
            raise ApplicationError(401, "Invalid test token")
        return Principal(self.account_id, False)


class FakeInventory:
    def snapshot(self, account_id):
        from vatican_backend.bootstrap.weapon_catalog import LoadCatalog
        return {"Configurations": LoadCatalog(), "Weapons": [{"InstanceId": str(uuid5(account_id, "weapon.starter.sword")),
            "ConfigurationId": "sword.common", "Level": 1, "Experience": 0}],
            "Loadout": [str(uuid5(account_id, "weapon.starter.sword"))], "Revision": 1}
