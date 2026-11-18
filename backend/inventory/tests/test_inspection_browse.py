from datetime import datetime, timezone
import hashlib
import json
import math
from uuid import UUID, uuid4

from dishka import Provider, Scope, provide
from fastapi.testclient import TestClient
import pytest
from sqlalchemy import select

from vatican_inventory.application.broker import AcceptBrokerDelivery, BrokerOperations, ProcessBrokerCommands
from vatican_inventory.application.browse import BrowseInventory
from vatican_inventory.application.errors import ApplicationError
from vatican_inventory.application.models import Principal
from vatican_inventory.application.ports import DefinitionsGateway
from vatican_inventory.bootstrap.settings import Settings
from vatican_inventory.domain.broker import Command, InspectionKind
from vatican_inventory.persistence.postgres.repositories.state import StateRepository
from vatican_inventory.persistence.postgres.tables import BrokerInbox, OutboxMessage
from vatican_inventory.presentation.app import create_app


def grant(inventory, kind, ref, quantity=1):
    return inventory.api.receive(uuid4(), "level.reward", 1, {"accountId": str(inventory.account),
        "claimId": str(uuid4()), "itemIndex": 0, "rewardType": kind, "rewardRef": ref, "quantity": quantity})


@pytest.fixture
def client(inventory):
    class Definitions(Provider):
        @provide(scope=Scope.APP, provides=DefinitionsGateway)
        def get(self) -> DefinitionsGateway:
            return inventory.definitions
    other = uuid4()
    class Auth:
        def authenticate(self, token):
            return Principal(other if token == "other" else inventory.account, True)
    with TestClient(create_app(Settings(inventory.url, "s" * 32), providers=(Definitions(),), auth_gateway=Auth())) as value:
        yield value


def test_owned_weapons_pages_are_content_independent_and_stable_for_equal_timestamps(inventory):
    grant(inventory, "WEAPON", "axe.rare", 7)
    expected = [row["InstanceId"] for row in inventory.api.get(inventory.account)["Weapons"]]
    inventory.definitions.down = True
    api, cursor, result = BrowseInventory(inventory.api.uow), None, []
    while True:
        page = api.weapons(inventory.account, 3, cursor)
        assert len(page["Weapons"]) <= 3
        result.extend(row["InstanceId"] for row in page["Weapons"])
        cursor = page["nextCursor"]
        if cursor is None:
            break
    assert result == expected and len(set(result)) == 8
    filtered = api.weapons(inventory.account, 100, configuration="axe.rare")
    assert len(filtered["Weapons"]) == 7


def test_weapon_page_cursor_is_bound_to_owner_filter_and_revision(inventory):
    grant(inventory, "WEAPON", "axe.rare", 3)
    api = BrowseInventory(inventory.api.uow)
    cursor = api.weapons(inventory.account, 1)["nextCursor"]
    for account, config in [(uuid4(), None), (inventory.account, "axe.rare")]:
        with pytest.raises(ApplicationError) as error:
            api.weapons(account, 1, cursor, config)
        assert error.value.status_code == 422
    grant(inventory, "ITEM", "kit")
    with pytest.raises(ApplicationError) as error:
        api.weapons(inventory.account, 1, cursor)
    assert error.value.status_code == 409


def test_items_filter_zero_balances_legacy_keys_and_revision_are_preserved(inventory):
    grant(inventory, "ITEM", "kit", 2)
    grant(inventory, "CASE", "daily", 2)
    with inventory.api.uow() as work:
        repo = work.repository
        repo.lock_player(inventory.account)
        for key in ("", "old item with spaces", "Я" * 160, "😀" * 160):
            repo.set_quantity(inventory.account, key, 0)
    api, keys, cursor = BrowseInventory(inventory.api.uow), [], None
    while True:
        page = api.items(inventory.account, 1, cursor)
        keys.extend(row["itemId"] for row in page["items"])
        cursor = page["nextCursor"]
        if cursor is None:
            break
        assert len(cursor) <= 2048
    assert len(keys) == len(set(keys)) == 6
    assert api.items(inventory.account, kind="CASE")["items"] == [{"itemId": "case:daily", "quantity": 2}]
    assert api.items(inventory.account, kind="ITEM")["items"] == [{"itemId": "item:kit", "quantity": 2}]
    first = api.items(inventory.account, 1)
    inventory.api.item_consumption.execute(inventory.account, uuid4(), "item:kit", 1)
    with pytest.raises(ApplicationError) as error:
        api.items(inventory.account, 1, first["nextCursor"])
    assert error.value.status_code == 409


@pytest.mark.parametrize("limit,cursor", [(0, None), (101, None), (True, None), (1, "bad"), (1, "x" * 2049)])
def test_invalid_inventory_pages_are_rejected(inventory, limit, cursor):
    with pytest.raises(ApplicationError) as error:
        BrowseInventory(inventory.api.uow).weapons(inventory.account, limit, cursor)
    assert error.value.status_code == 422


def test_run_snapshot_and_progression_read_only_pinned_instances(inventory, monkeypatch):
    axe = inventory.api.grant(inventory.account, uuid4(), "axe.rare")
    inventory.api.set_loadout(inventory.account, [UUID(axe["instanceId"])])
    def fail(*args):
        raise AssertionError("Do not load all owned weapons to handle a selected run")
    monkeypatch.setattr(StateRepository, "owned", fail)
    snapshot = inventory.api.get(inventory.account, selected_only=True)
    assert snapshot["Loadout"] == [axe["instanceId"]] and len(snapshot["Weapons"]) == 1
    inventory.api.receive(uuid4(), "run.weapon_reward", 1, {"accountId": str(inventory.account),
        "runId": str(uuid4()), "configurationId": "sword.common", "snapshot": snapshot})
    detail = inventory.api.weapon(inventory.account, UUID(axe["instanceId"]))
    assert detail["Weapon"]["Experience"] == 50


def accepted(inventory, count=1):
    receiver = AcceptBrokerDelivery(inventory.api.uow)
    ids = []
    for _ in range(count):
        identity = uuid4()
        receiver.execute(json.dumps({"messageId": str(identity), "eventName": "unsupported.command",
            "schemaVersion": 1, "payload": {"PRIVATE": "secret"}}).encode())
        ids.append(str(identity))
    return ids


def test_broker_lists_have_stable_pagination_filters_and_do_not_expose_payloads(inventory):
    identities = accepted(inventory, 5)
    ops = BrokerOperations(inventory.api.uow)
    # Exercise UUID tie-breaking, including cursors whose source row later changes status.
    with inventory.database.transaction() as session:
        for row in session.scalars(select(BrokerInbox)):
            row.created_at = datetime(2026, 1, 1, tzinfo=timezone.utc)
    seen, cursor = [], None
    while True:
        page = ops.list(InspectionKind.COMMANDS, 2, cursor)
        assert "PRIVATE" not in json.dumps(page) and "secret" not in json.dumps(page)
        seen.extend(row["messageId"] for row in page["commands"])
        cursor = page["nextCursor"]
        if cursor is None:
            break
    assert seen == sorted(identities, reverse=True)
    ProcessBrokerCommands(inventory.api.uow, inventory.api).once()
    assert len(ops.list(InspectionKind.COMMANDS, state="REJECTED")["commands"]) == 1
    page = ops.list(InspectionKind.COMMANDS, 1, state="PENDING")
    with pytest.raises(ApplicationError):
        ops.list(InspectionKind.COMMANDS, 1, page["nextCursor"], "REVIEW")


def test_quarantine_inspection_discovers_failed_event_for_redrive_without_raw_body(inventory):
    receiver, ops = AcceptBrokerDelivery(inventory.api.uow), BrokerOperations(inventory.api.uow)
    receiver.execute(b"SECRET password is malformed JSON")
    page = ops.list(InspectionKind.REJECTIONS)
    assert "SECRET" not in json.dumps(page) and "password" not in json.dumps(page)
    row = page["rejections"][0]
    assert row["bodySize"] == 33 and row["bodySha256"] == hashlib.sha256(b"SECRET password is malformed JSON").hexdigest()
    identity, event = UUID(row["rejectionId"]), UUID(row["event"]["eventId"])
    assert ops.detail(InspectionKind.REJECTIONS, identity) == row
    with inventory.database.transaction() as session:
        source = session.get(OutboxMessage, event)
        source.attempts, source.failed_at, source.last_error = 20, datetime.now(timezone.utc), "PublicationUnavailable"
    failed = ops.list(InspectionKind.EVENTS, state="REVIEW")["events"]
    assert len(failed) == 1 and failed[0]["eventId"] == str(event)
    assert ops.replay(UUID(failed[0]["eventId"]), outbox=True)
    assert ops.detail(InspectionKind.EVENTS, event)["status"] == "PENDING"


@pytest.mark.parametrize("kind,state", [("bad", None), (InspectionKind.COMMANDS, "bad"),
    (InspectionKind.EVENTS, "APPLIED"), (InspectionKind.REJECTIONS, "PRIVATE")])
def test_invalid_operational_filters_are_rejected(inventory, kind, state):
    with pytest.raises(ApplicationError) as error:
        BrokerOperations(inventory.api.uow).list(kind, state=state)
    assert error.value.status_code == 422


def test_operational_detail_hides_foreign_destinations_and_missing_records(inventory):
    identity = uuid4()
    with inventory.database.transaction() as session:
        session.add(OutboxMessage(id=identity, destination="analytics", event_name="legacy", schema_version=1,
                                 aggregate_id=uuid4(), payload={"PRIVATE": "secret"}))
    ops = BrokerOperations(inventory.api.uow)
    assert ops.list(InspectionKind.EVENTS)["events"] == []
    for kind, value in [(InspectionKind.EVENTS, identity), (InspectionKind.REJECTIONS, uuid4())]:
        with pytest.raises(ApplicationError) as error:
            ops.detail(kind, value)
        assert error.value.status_code == 404


def test_http_pages_compatibility_internal_diagnostics_and_auth(client, inventory):
    grant(inventory, "ITEM", "a")
    grant(inventory, "ITEM", "b")
    grant(inventory, "WEAPON", "axe.rare", 2)
    player = {"Authorization": "Bearer player"}
    original = client.get("/v1/inventory", headers=player).json()
    assert set(original) == {"items", "revision"}
    inventory.definitions.down = True
    weapons = client.get("/v1/weapons/owned?limit=1", headers=player)
    assert weapons.status_code == 200 and weapons.json()["nextCursor"]
    foreign = client.get("/v1/weapons/owned", headers={"Authorization": "Bearer other"},
                         params={"cursor": weapons.json()["nextCursor"]})
    assert foreign.status_code == 422
    assert client.get("/v1/weapons/owned").status_code == 401
    items = client.get("/v1/inventory?limit=1&kind=ITEM", headers=player).json()
    assert len(items["items"]) == 1 and items["nextCursor"]
    assert client.get("/v1/inventory", headers=player, params={"limit": 10, "cursor": items["nextCursor"], "kind": "ITEM"}).json()["nextCursor"] is None
    AcceptBrokerDelivery(inventory.api.uow).execute(b"PRIVATE invalid JSON")
    service = {"X-Service-Secret": "s" * 32}
    for name in ("commands", "events", "rejections"):
        path = "/internal/broker/" + name
        assert client.get(path, headers=player).status_code == 401
        assert client.get(path, headers=service).status_code == 200
    rejection = client.get("/internal/broker/rejections", headers=service).json()["rejections"][0]
    assert client.get("/internal/broker/rejections/" + rejection["rejectionId"], headers=service).json() == rejection
    assert client.get("/internal/broker/events/" + rejection["event"]["eventId"], headers=service).status_code == 200
    assert client.get("/internal/broker/commands?status=bad", headers=service).status_code == 422


def test_lossless_command_storage_replays_an_http_run_with_float_and_negative_zero(inventory):
    snapshot = inventory.api.get(inventory.account, selected_only=True)
    snapshot["Configurations"][0].update(Damage=1e30, SignedZero=-0.0)
    value = {"messageId": str(uuid4()), "eventName": "run.weapon_reward", "schemaVersion": 1,
        "payload": {"accountId": str(inventory.account), "runId": str(uuid4()),
                    "configurationId": "axe.rare", "snapshot": snapshot}}
    identity = UUID(value["messageId"])
    first = inventory.api.receive(identity, value["eventName"], 1, value["payload"])
    AcceptBrokerDelivery(inventory.api.uow).execute(json.dumps(value).encode())
    with inventory.database.transaction() as session:
        row = session.get(BrokerInbox, identity)
        definition = row.envelope["payload"]["snapshot"]["Configurations"][0]
        assert type(definition["Damage"]) is float and math.copysign(1, definition["SignedZero"]) == -1
        assert Command.parse(json.dumps(row.envelope).encode()).digest == row.payload_hash
    inventory.definitions.down = True
    assert ProcessBrokerCommands(inventory.api.uow, inventory.api).once()
    result = BrokerOperations(inventory.api.uow).get(identity)
    assert result["status"] == "APPLIED" and result["result"] == first
