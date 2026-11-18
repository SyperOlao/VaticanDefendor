from concurrent.futures import ThreadPoolExecutor
from copy import deepcopy
from uuid import UUID, uuid4, uuid5

import pytest
from sqlalchemy import func, select, text
from sqlalchemy.exc import IntegrityError

from vatican_inventory.application.errors import ApplicationError
from vatican_inventory.persistence.postgres.tables import (
    ConsumedMessage, InventoryState, OwnedWeapon, RunRewardReceipt,
)


def run_payload(inventory):
    return {"accountId": str(inventory.account), "runId": str(uuid4()), "configurationId": "axe.rare",
            "snapshot": inventory.api.get(inventory.account, selected_only=True)}


def receive(inventory, payload, message=None):
    return inventory.api.receive(message or uuid4(), "run.weapon_reward", 1, payload)


def test_concurrent_initialization_grants_one_starter_and_loadout(inventory):
    with ThreadPoolExecutor(max_workers=6) as workers:
        replies = list(workers.map(lambda _: inventory.api.get(inventory.account), range(6)))
    assert all(reply == replies[0] for reply in replies)
    assert replies[0]["Loadout"] == [str(uuid5(inventory.account, "weapon.starter.sword"))]
    assert len(replies[0]["Weapons"]) == 1 and replies[0]["Revision"] == 1


def test_run_reward_uses_pinned_rules_and_replays_without_content(inventory):
    payload = run_payload(inventory)
    inventory.definitions.values[0]["ExperiencePerLevel"] = 999
    inventory.definitions.values[0]["MaximumLevel"] = 1
    first_message = uuid4()
    first = receive(inventory, payload, first_message)
    starter = next(weapon for weapon in inventory.api.get(inventory.account)["Weapons"]
                   if weapon["ConfigurationId"] == "sword.common")
    assert (starter["Level"], starter["Experience"]) == (2, 100)
    inventory.definitions.down = True
    assert receive(inventory, payload, first_message) == first
    assert receive(inventory, payload)["receipt"] == first["receipt"]
    with inventory.database.transaction() as session:
        assert session.scalar(select(func.count()).select_from(OwnedWeapon)) == 2
        assert session.scalar(select(func.count()).select_from(RunRewardReceipt)) == 1


def test_concurrent_new_message_ids_for_one_run_apply_once(inventory):
    payload = run_payload(inventory)
    with ThreadPoolExecutor(max_workers=6) as workers:
        replies = list(workers.map(lambda _: receive(inventory, payload), range(6)))
    assert all(reply["receipt"] == replies[0]["receipt"] for reply in replies)
    state = inventory.api.get(inventory.account)
    assert len(state["Weapons"]) == 2 and state["Revision"] == 2
    assert next(weapon for weapon in state["Weapons"] if weapon["ConfigurationId"] == "sword.common")["Experience"] == 100


@pytest.mark.parametrize("field", ["accountId", "configurationId", "snapshot"])
def test_run_source_rejects_changed_payload_with_a_new_message_id(inventory, field):
    payload = run_payload(inventory)
    first = receive(inventory, payload)
    changed = deepcopy(payload)
    if field == "accountId":
        changed[field] = str(uuid4())
    elif field == "configurationId":
        changed[field] = "sword.common"
    else:
        changed[field]["Revision"] += 1
    with pytest.raises(ApplicationError) as error:
        receive(inventory, changed)
    assert error.value.status_code == 409
    assert receive(inventory, payload)["receipt"] == first["receipt"]


def test_unknown_reward_can_be_retried_after_content_recovers(inventory):
    payload = run_payload(inventory)
    inventory.definitions.values.pop()
    with pytest.raises(ApplicationError) as error:
        receive(inventory, payload)
    assert error.value.status_code == 422
    assert inventory.api.get(inventory.account)["Revision"] == 1
    inventory.definitions.values.append({"Id": "axe.rare", "ExperiencePerLevel": 50, "MaximumLevel": 10})
    assert receive(inventory, payload)["status"] == "APPLIED"


def test_foreign_pinned_weapon_rolls_back_every_change(inventory):
    payload = run_payload(inventory)
    foreign = inventory.api.get(uuid4())
    payload["snapshot"]["Weapons"] = foreign["Weapons"]
    payload["snapshot"]["Loadout"] = foreign["Loadout"]
    with pytest.raises(ApplicationError) as error:
        receive(inventory, payload)
    assert error.value.status_code == 409
    own = inventory.api.get(inventory.account)
    assert own["Revision"] == 1 and own["Weapons"][0]["Experience"] == 0


def test_receipt_commit_failure_rolls_back_weapon_xp_and_deduplication(inventory):
    payload, message = run_payload(inventory), uuid4()
    with inventory.database.engine.begin() as connection:
        connection.execute(text("ALTER TABLE run_reward_receipts ADD CONSTRAINT injected_receipt_failure CHECK (false)"))
    try:
        with pytest.raises(IntegrityError):
            receive(inventory, payload, message)
        before = inventory.api.get(inventory.account)
        assert len(before["Weapons"]) == 1 and before["Weapons"][0]["Experience"] == 0 and before["Revision"] == 1
        with inventory.database.transaction() as session:
            assert session.get(ConsumedMessage, ("rewards.v1", message)) is None
    finally:
        with inventory.database.engine.begin() as connection:
            connection.execute(text("ALTER TABLE run_reward_receipts DROP CONSTRAINT injected_receipt_failure"))
    assert receive(inventory, payload, message)["status"] == "APPLIED"


def test_progression_applies_to_pinned_loadout_after_player_equips_another_weapon(inventory):
    payload = run_payload(inventory)
    grant = inventory.api.grant(inventory.account, uuid4(), "axe.rare")
    inventory.api.set_loadout(inventory.account, [UUID(grant["instanceId"])])
    receive(inventory, payload)
    state = inventory.api.get(inventory.account)
    assert state["Loadout"] == [grant["instanceId"]]
    assert next(w for w in state["Weapons"] if w["InstanceId"] == payload["snapshot"]["Loadout"][0])["Experience"] == 100
    assert next(w for w in state["Weapons"] if w["InstanceId"] == grant["instanceId"])["Experience"] == 0


def test_loadout_order_noop_ownership_and_revision(inventory):
    first = inventory.api.get(inventory.account)
    starter = UUID(first["Loadout"][0])
    second = UUID(inventory.api.grant(inventory.account, uuid4(), "axe.rare")["instanceId"])
    revision = inventory.api.get(inventory.account)["Revision"]
    result = inventory.api.set_loadout(inventory.account, [second, starter], revision)
    assert result["Loadout"] == [str(second), str(starter)] and result["Revision"] == revision + 1
    assert inventory.api.set_loadout(inventory.account, [second, starter]) == result
    for ids, expected, status in [([starter, starter], None, 422), ([uuid4()], None, 403), ([starter], revision, 409)]:
        with pytest.raises(ApplicationError) as error:
            inventory.api.set_loadout(inventory.account, ids, expected)
        assert error.value.status_code == status
    assert inventory.api.get(inventory.account) == result


def test_competing_loadout_updates_with_same_revision_allow_one_writer(inventory):
    starter = UUID(inventory.api.get(inventory.account)["Loadout"][0])
    axe = UUID(inventory.api.grant(inventory.account, uuid4(), "axe.rare")["instanceId"])
    revision = inventory.api.get(inventory.account)["Revision"]
    def update(ids):
        try:
            return inventory.api.set_loadout(inventory.account, ids, revision)["Revision"]
        except ApplicationError as error:
            return error.status_code
    with ThreadPoolExecutor(max_workers=2) as workers:
        results = list(workers.map(update, [[axe], [axe, starter]]))
    assert sorted(results) == [revision + 1, 409]


def test_concurrent_purchase_receipt_remains_stable_after_content_disappears(inventory):
    operation = uuid4()
    with ThreadPoolExecutor(max_workers=5) as workers:
        replies = list(workers.map(lambda _: inventory.api.grant(inventory.account, operation, "axe.rare"), range(5)))
    assert all(reply == replies[0] for reply in replies)
    inventory.definitions.down = True
    assert inventory.api.grant(inventory.account, operation, "axe.rare") == replies[0]
    with pytest.raises(ApplicationError) as error:
        inventory.api.grant(inventory.account, operation, "sword.common")
    assert error.value.status_code == 409


def test_shared_message_id_with_conflicting_sources_is_a_durable_conflict(inventory):
    message = uuid4()
    payloads = [{"accountId": str(uuid4()), "claimId": str(uuid4()), "itemIndex": 0,
                 "rewardType": "ITEM", "rewardRef": "kit", "quantity": 1} for _ in range(2)]
    def deliver(payload):
        try:
            return inventory.api.receive(message, "level.reward", 1, payload)["status"]
        except ApplicationError as error:
            return error.status_code
    with ThreadPoolExecutor(max_workers=2) as workers:
        results = list(workers.map(deliver, payloads))
    assert set(results) == {"APPLIED", 409}
    assert sum(len(inventory.api.items(UUID(payload["accountId"]))["items"]) for payload in payloads) == 1


def test_content_outage_does_not_initialize_a_half_read_inventory(inventory):
    inventory.definitions.values[0]["ExperiencePerLevel"] = 0
    with pytest.raises(ApplicationError) as error:
        inventory.api.get(inventory.account)
    assert error.value.status_code == 503
    with inventory.database.transaction() as session:
        assert session.get(InventoryState, inventory.account) is None


def test_game_snapshot_ignores_an_unselected_retired_weapon(inventory):
    inventory.api.grant(inventory.account, uuid4(), "axe.rare")
    inventory.definitions.values.pop()
    assert len(inventory.api.get(inventory.account, selected_only=True)["Weapons"]) == 1
    with pytest.raises(ApplicationError):
        inventory.api.get(inventory.account)


def test_stack_capacity_conflict_is_atomic_and_case_rejection_stays_rejected_after_funding(inventory):
    def award(kind, ref, quantity):
        return inventory.api.receive(uuid4(), "level.reward", 1, {
            "accountId": str(inventory.account), "claimId": str(uuid4()), "itemIndex": 0,
            "rewardType": kind, "rewardRef": ref, "quantity": quantity})
    award("ITEM", "kit", 9_223_372_036_854_775_807)
    before = inventory.api.items(inventory.account)
    with pytest.raises(ApplicationError) as error:
        award("ITEM", "kit", 1)
    assert error.value.status_code == 409
    assert inventory.api.items(inventory.account) == before

    payload = {"accountId": str(inventory.account), "openingId": str(uuid4()), "caseCode": "daily", "quantity": 1}
    rejected = inventory.api.receive(uuid4(), "case.consume", 1, payload)["receipt"]
    assert rejected["outcome"] == "REJECTED" and rejected["code"] == "InsufficientCases"
    award("CASE", "daily", 1)
    assert inventory.api.receive(uuid4(), "case.consume", 1, payload)["receipt"] == rejected
    applied = inventory.api.receive(uuid4(), "case.consume", 1, {**payload, "openingId": str(uuid4())})["receipt"]
    assert applied["outcome"] == "APPLIED" and applied["quantityAfter"] == 0


def test_initialization_preserves_an_existing_nonstarter_loadout(inventory):
    account, instance = inventory.account, uuid4()
    from vatican_inventory.persistence.postgres.tables import WeaponLoadout
    with inventory.database.transaction() as session:
        session.add(InventoryState(account_id=account, revision=5))
        session.add(OwnedWeapon(instance_id=instance, account_id=account, configuration_id="axe.rare", grant_key="imported"))
        session.flush()
        session.add(WeaponLoadout(account_id=account, slot=0, instance_id=instance))
    result = inventory.api.get(account)
    assert result["Loadout"] == [str(instance)] and len(result["Weapons"]) == 2 and result["Revision"] == 6
    assert inventory.api.get(account) == result
