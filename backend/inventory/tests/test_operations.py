from concurrent.futures import ThreadPoolExecutor
from uuid import UUID, uuid4

import pytest
from sqlalchemy import func, select

from vatican_inventory.application.catalog import Operation
from vatican_inventory.application.errors import ApplicationError
from vatican_inventory.persistence.postgres.repositories.state import StateRepository
from vatican_inventory.persistence.postgres.tables import (
    ConsumedMessage, IdempotencyRecord, InventoryState,
)


def award(inventory, quantity=5, account=None):
    inventory.api.receive(uuid4(), "level.reward", 1, {
        "accountId": str(account or inventory.account), "claimId": str(uuid4()), "itemIndex": 0,
        "rewardType": "ITEM", "rewardRef": "repair-kit", "quantity": quantity,
    })


def test_consumption_replays_across_message_and_direct_transports_without_content(inventory):
    award(inventory)
    inventory.definitions.down = True
    operation, message = uuid4(), uuid4()
    payload = {"accountId": str(inventory.account), "operationId": str(operation), "itemId": "item:repair-kit", "quantity": 2}
    first = inventory.api.item_consumption.execute(inventory.account, operation, payload["itemId"], 2)
    assert first["status"] == "APPLIED" and first["quantityAfter"] == 3
    assert inventory.api.receive(message, "inventory.item.consume", 1, payload)["receipt"] == first
    assert inventory.api.receive(uuid4(), "inventory.item.consume", 1, payload)["receipt"] == first
    assert inventory.api.item_consumption.get(inventory.account, operation) == first
    assert inventory.api.item(inventory.account, payload["itemId"])["quantity"] == 3
    with pytest.raises(ApplicationError) as error:
        inventory.api.item_consumption.execute(inventory.account, operation, payload["itemId"], 1)
    assert error.value.status_code == 409


def test_consumption_rejection_is_durable_after_restock_and_scoped_to_owner(inventory):
    operation = uuid4()
    consume = inventory.api.item_consumption.execute
    first = consume(inventory.account, operation, "item:repair-kit", 2)
    assert first["status"] == "REJECTED" and first["code"] == "InsufficientItems"
    award(inventory)
    assert consume(inventory.account, operation, "item:repair-kit", 2) == first
    assert inventory.api.item(inventory.account, "item:repair-kit")["quantity"] == 5
    other = uuid4()
    award(inventory, account=other)
    assert consume(other, operation, "item:repair-kit", 2)["status"] == "APPLIED"


def test_concurrent_consumptions_cannot_spend_the_same_items_twice(inventory):
    award(inventory, 3)
    consume = inventory.api.item_consumption.execute
    with ThreadPoolExecutor(max_workers=6) as pool:
        results = list(pool.map(lambda _: consume(inventory.account, uuid4(), "item:repair-kit", 2), range(6)))
    assert sum(result["status"] == "APPLIED" for result in results) == 1
    assert sum(result["status"] == "REJECTED" for result in results) == 5
    assert inventory.api.item(inventory.account, "item:repair-kit")["quantity"] == 1


def test_concurrent_retries_of_one_consumption_share_one_receipt(inventory):
    award(inventory)
    operation = uuid4()
    with ThreadPoolExecutor(max_workers=5) as pool:
        results = list(pool.map(lambda _: inventory.api.item_consumption.execute(
            inventory.account, operation, "item:repair-kit", 2), range(5)))
    assert all(result == results[0] for result in results)
    assert inventory.api.item(inventory.account, "item:repair-kit")["quantity"] == 3


def test_failed_receipt_write_rolls_back_consumption_revision_and_message_alias(inventory, monkeypatch):
    award(inventory)
    before = inventory.api.items(inventory.account)
    operation, message = uuid4(), uuid4()
    payload = {"accountId": str(inventory.account), "operationId": str(operation), "itemId": "item:repair-kit", "quantity": 2}
    record = StateRepository.record_command
    def fail(*args, **kwargs):
        record(*args, **kwargs)
        raise RuntimeError("Injected failure after debit")
    monkeypatch.setattr(StateRepository, "record_command", fail)
    with pytest.raises(RuntimeError):
        inventory.api.receive(message, "inventory.item.consume", 1, payload)
    assert inventory.api.items(inventory.account) == before
    with inventory.database.transaction() as session:
        assert session.get(ConsumedMessage, ("rewards.v1", message)) is None
        assert session.get(IdempotencyRecord, (inventory.account, Operation.ITEM_CONSUMPTION, str(operation))) is None
    monkeypatch.setattr(StateRepository, "record_command", record)
    assert inventory.api.receive(message, "inventory.item.consume", 1, payload)["receipt"]["quantityAfter"] == 3


@pytest.mark.parametrize("item,quantity", [("case:daily", 1), ("bad/item", 1), ([], 1),
    ("item:kit", True), ("item:kit", 0), ("item:kit", -1), ("item:kit", 1.5),
    ("item:kit", "1"), ("item:kit", 9_223_372_036_854_775_808)])
def test_invalid_consumptions_are_rejected_before_state_is_created(inventory, item, quantity):
    with pytest.raises(ApplicationError) as error:
        inventory.api.item_consumption.execute(inventory.account, uuid4(), item, quantity)
    assert error.value.status_code == 422
    with inventory.database.transaction() as session:
        assert session.get(InventoryState, inventory.account) is None


def test_legacy_item_keys_can_be_consumed_without_renaming(inventory):
    with inventory.api.uow() as work:
        repo = work.repository
        repo.lock_player(inventory.account)
        repo.ensure(inventory.account)
        repo.set_quantity(inventory.account, "legacy-kit", 3)
    receipt = inventory.api.item_consumption.execute(inventory.account, uuid4(), "legacy-kit", 1)
    assert receipt["quantityAfter"] == 2


def test_keyed_loadout_replay_survives_lost_ack_outage_and_a_newer_selection(inventory):
    starter = UUID(inventory.api.get(inventory.account)["Loadout"][0])
    axe = UUID(inventory.api.grant(inventory.account, uuid4(), "axe.rare")["instanceId"])
    revision = inventory.api.get(inventory.account)["Revision"]
    key = uuid4()
    first = inventory.api.set_loadout(inventory.account, [axe], revision, key)
    confirmation = first["loadoutOperation"]
    assert confirmation["inventoryRevision"] == revision + 1
    inventory.definitions.down = True
    assert inventory.api.set_loadout(inventory.account, [axe], revision, key) == first
    assert inventory.api.loadout_result(inventory.account, key) == confirmation
    inventory.definitions.down = False
    newer = inventory.api.set_loadout(inventory.account, [starter])
    assert inventory.api.set_loadout(inventory.account, [axe], revision, key) == first
    assert inventory.api.get(inventory.account) == newer
    for ids, expected in [([starter], revision), ([axe], revision + 1)]:
        with pytest.raises(ApplicationError) as error:
            inventory.api.set_loadout(inventory.account, ids, expected, key)
        assert error.value.status_code == 409


def test_same_loadout_key_is_atomic_under_concurrency(inventory):
    starter = UUID(inventory.api.get(inventory.account)["Loadout"][0])
    axe = UUID(inventory.api.grant(inventory.account, uuid4(), "axe.rare")["instanceId"])
    revision, key = inventory.api.get(inventory.account)["Revision"], uuid4()
    with ThreadPoolExecutor(max_workers=5) as pool:
        results = list(pool.map(lambda _: inventory.api.set_loadout(inventory.account, [axe, starter], revision, key), range(5)))
    assert all(result == results[0] for result in results)
    assert inventory.api.get(inventory.account)["Revision"] == revision + 1
    with inventory.database.transaction() as session:
        assert session.scalar(select(func.count()).select_from(IdempotencyRecord)
                              .where(IdempotencyRecord.operation == Operation.LOADOUT)) == 1


def test_failed_loadout_receipt_rolls_back_the_selection_and_retry_can_complete(inventory, monkeypatch):
    starter = UUID(inventory.api.get(inventory.account)["Loadout"][0])
    axe = UUID(inventory.api.grant(inventory.account, uuid4(), "axe.rare")["instanceId"])
    before = inventory.api.get(inventory.account)
    record, key = StateRepository.record_command, uuid4()
    def fail(*args, **kwargs):
        raise RuntimeError("Injected loadout receipt failure")
    monkeypatch.setattr(StateRepository, "record_command", fail)
    with pytest.raises(RuntimeError):
        inventory.api.set_loadout(inventory.account, [axe], before["Revision"], key)
    assert inventory.api.get(inventory.account) == before and before["Loadout"] == [str(starter)]
    monkeypatch.setattr(StateRepository, "record_command", record)
    assert inventory.api.set_loadout(inventory.account, [axe], before["Revision"], key)["Loadout"] == [str(axe)]


def test_missing_operation_and_foreign_weapon_reads_do_not_create_inventory(inventory):
    for call in [lambda: inventory.api.loadout_result(inventory.account, uuid4()),
                 lambda: inventory.api.grant_result(inventory.account, uuid4()),
                 lambda: inventory.api.item_consumption.get(inventory.account, uuid4()),
                 lambda: inventory.api.item(inventory.account, "item:missing"),
                 lambda: inventory.api.weapon(inventory.account, uuid4())]:
        with pytest.raises(ApplicationError) as error:
            call()
        assert error.value.status_code == 404
    with inventory.database.transaction() as session:
        assert session.get(InventoryState, inventory.account) is None
