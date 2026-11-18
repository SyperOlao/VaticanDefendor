from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone
import json
from types import SimpleNamespace
from uuid import UUID, uuid4

import pytest
from sqlalchemy import func, select

from vatican_inventory.application.broker import AcceptBrokerDelivery, BrokerOperations, ProcessBrokerCommands, PublishBrokerEvents
from vatican_inventory.application.errors import ApplicationError
from vatican_inventory.domain.broker import BrokerCode, Destination, InboxStatus, MAX_BODY_BYTES, rejection_id
from vatican_inventory.persistence.postgres.repositories.broker import BrokerRepository
from vatican_inventory.persistence.postgres.tables import BrokerInbox, BrokerRejection, OutboxMessage


def envelope(inventory, event="level.reward", payload=None, identity=None):
    return {"messageId": str(identity or uuid4()), "eventName": event, "schemaVersion": 1,
        "payload": payload or {"accountId": str(inventory.account), "claimId": str(uuid4()), "itemIndex": 0,
                               "rewardType": "ITEM", "rewardRef": "kit", "quantity": 3}}


def ready_again(inventory):
    with inventory.database.transaction() as session:
        for table in (BrokerInbox, OutboxMessage):
            for row in session.scalars(select(table)):
                row.available_at = datetime.now(timezone.utc) - timedelta(seconds=1)


@pytest.fixture
def pipeline(inventory):
    class Publisher:
        events = []
        fail = False
        def publish(self, event):
            assert getattr(inventory.active, "depth", 0) == 0
            if self.fail:
                raise RuntimeError("PRIVATE SQL password token must never enter outbox errors")
            self.events.append(event)
    publisher = Publisher()
    return SimpleNamespace(accept=AcceptBrokerDelivery(inventory.api.uow),
        process=ProcessBrokerCommands(inventory.api.uow, inventory.api),
        publish=PublishBrokerEvents(inventory.api.uow, publisher), publisher=publisher,
        operations=BrokerOperations(inventory.api.uow))


def accept(pipeline, value):
    pipeline.accept.execute(json.dumps(value).encode(), value["messageId"], "application/json", "utf-8")


def test_duplicate_acceptance_and_new_message_source_replay_have_one_business_effect(inventory, pipeline):
    value = envelope(inventory)
    with ThreadPoolExecutor(max_workers=5) as pool:
        list(pool.map(lambda _: accept(pipeline, value), range(5)))
    assert pipeline.process.once() and not pipeline.process.once()
    assert inventory.api.item(inventory.account, "item:kit")["quantity"] == 3
    second = {**value, "messageId": str(uuid4())}
    accept(pipeline, second)
    assert pipeline.process.once()
    # Replayed HTTP and Rabbit commands share the existing source receipt.
    inventory.api.receive(uuid4(), value["eventName"], 1, value["payload"])
    assert inventory.api.item(inventory.account, "item:kit")["quantity"] == 3
    with inventory.database.transaction() as session:
        assert session.scalar(select(func.count()).select_from(BrokerInbox)) == 2
        assert session.scalar(select(func.count()).select_from(OutboxMessage)) == 2
    assert pipeline.publish.once()
    event = pipeline.publisher.events[0]
    assert event.payload["status"] == "APPLIED" and event.payload["deliveryGeneration"] == 1


def test_same_message_id_with_changed_payload_is_quarantined_without_replacing_original(inventory, pipeline):
    value = envelope(inventory)
    accept(pipeline, value)
    accept(pipeline, {**value, "payload": {**value["payload"], "quantity": 99}})
    pipeline.process.once()
    assert inventory.api.item(inventory.account, "item:kit")["quantity"] == 3
    with inventory.database.transaction() as session:
        assert session.scalar(select(func.count()).select_from(BrokerInbox)) == 1
        assert session.scalar(select(BrokerRejection.code)) == BrokerCode.MESSAGE_CONFLICT
    assert pipeline.operations.get(UUID(value["messageId"]))["status"] == InboxStatus.APPLIED


@pytest.mark.parametrize("raw,content_type", [
    (b"not-json SECRET", "application/json"), (b"[]", None), (b"{}", None),
    (b'{"messageId":"x","messageId":"y"}', None), (b"x" * (MAX_BODY_BYTES + 1), None),
    (b"{}", "text/plain"),
])
def test_invalid_delivery_has_bounded_durable_quarantine_and_no_business_work(inventory, pipeline, raw, content_type):
    pipeline.accept.execute(raw, content_type=content_type)
    pipeline.accept.execute(raw, content_type=content_type)
    assert not pipeline.process.once()
    with inventory.database.transaction() as session:
        row = session.scalar(select(BrokerRejection))
        assert row.body_size == len(raw) and len(row.body_prefix) <= 4096
        assert session.scalar(select(func.count()).select_from(BrokerRejection)) == 1
        assert session.scalar(select(func.count()).select_from(BrokerInbox)) == 0
    pipeline.publish.once()
    assert pipeline.publisher.events[0].destination == Destination.QUARANTINE
    assert "SECRET" not in json.dumps(pipeline.publisher.events[0].payload)


@pytest.mark.parametrize("patch", [{"bad": "\x00"}, {"bad": "\ud800"}, {"bad": float("nan")}])
def test_json_values_postgres_cannot_store_are_quarantined_not_requeued_forever(inventory, pipeline, patch):
    value = envelope(inventory)
    value["payload"].update(patch)
    pipeline.accept.execute(json.dumps(value).encode())
    with inventory.database.transaction() as session:
        assert session.scalar(select(func.count()).select_from(BrokerRejection)) == 1


def test_amqp_message_id_mismatch_is_rejected(inventory, pipeline):
    value = envelope(inventory)
    pipeline.accept.execute(json.dumps(value).encode(), str(uuid4()))
    with inventory.database.transaction() as session:
        assert session.scalar(select(BrokerRejection.code)) == BrokerCode.MESSAGE_ID_MISMATCH


def test_crash_after_business_commit_recovers_from_source_receipt_without_second_weapon(inventory, pipeline, monkeypatch):
    value = envelope(inventory, "inventory.weapon.grant", {
        "accountId": str(inventory.account), "operationId": str(uuid4()), "configurationId": "axe.rare"})
    accept(pipeline, value)
    original = BrokerRepository.finish_command
    def fail(*args, **kwargs):
        raise RuntimeError("Injected crash after inventory commit")
    monkeypatch.setattr(BrokerRepository, "finish_command", fail)
    with pytest.raises(RuntimeError):
        pipeline.process.once()
    before = inventory.api.get(inventory.account)
    assert len(before["Weapons"]) == 2
    monkeypatch.setattr(BrokerRepository, "finish_command", original)
    ready_again(inventory)
    inventory.definitions.down = True
    assert pipeline.process.once()
    assert pipeline.operations.get(UUID(value["messageId"]))["status"] == InboxStatus.APPLIED
    inventory.definitions.down = False
    assert inventory.api.get(inventory.account) == before


def test_transient_content_failure_backoff_review_and_operator_replay_keep_original_payload(inventory, pipeline):
    value = envelope(inventory, "inventory.weapon.grant", {
        "accountId": str(inventory.account), "operationId": str(uuid4()), "configurationId": "axe.rare"})
    accept(pipeline, value)
    inventory.definitions.down = True
    assert pipeline.process.once() and not pipeline.process.once()
    identity = UUID(value["messageId"])
    assert pipeline.operations.get(identity)["status"] == InboxStatus.PENDING
    with inventory.database.transaction() as session:
        row = session.get(BrokerInbox, identity)
        row.attempts = 19
    ready_again(inventory)
    pipeline.process.once()
    assert pipeline.operations.get(identity)["status"] == InboxStatus.REVIEW
    assert not pipeline.process.once()
    inventory.definitions.down = False
    assert pipeline.operations.replay(identity)
    assert not pipeline.operations.replay(identity)
    pipeline.process.once()
    status = pipeline.operations.get(identity)
    assert status["status"] == InboxStatus.APPLIED and status["deliveryGeneration"] == 2
    with inventory.database.transaction() as session:
        assert session.get(BrokerInbox, identity).envelope == value
        events = list(session.scalars(select(OutboxMessage).where(OutboxMessage.destination == Destination.RESULT)))
        assert len(events) == 2 and events[0].id != events[1].id


def test_invalid_command_is_terminal_but_business_rejection_is_applied(inventory, pipeline):
    invalid = envelope(inventory, "unsupported.command")
    accept(pipeline, invalid)
    pipeline.process.once()
    assert pipeline.operations.get(UUID(invalid["messageId"]))["status"] == InboxStatus.REJECTED
    rejected = envelope(inventory, "case.consume", {"accountId": str(inventory.account),
        "openingId": str(uuid4()), "caseCode": "missing", "quantity": 1})
    accept(pipeline, rejected)
    pipeline.process.once()
    result = pipeline.operations.get(UUID(rejected["messageId"]))
    assert result["status"] == InboxStatus.APPLIED and result["result"]["receipt"]["outcome"] == "REJECTED"


def test_expired_command_lease_cannot_finish_a_newer_attempt(inventory, pipeline):
    value = envelope(inventory)
    accept(pipeline, value)
    with inventory.api.uow() as work:
        old = work.broker.claim_command()
    ready_again(inventory)
    with inventory.api.uow() as work:
        newer = work.broker.claim_command()
        assert newer.lease != old.lease
        assert not work.broker.finish_command(old, InboxStatus.APPLIED, {"status": "APPLIED"})
        work.broker.retry_command(old, BrokerCode.HANDLER_FAILED)
    assert pipeline.operations.get(UUID(value["messageId"]))["status"] == InboxStatus.PENDING
    ready_again(inventory)
    assert pipeline.process.once()


def test_lost_publication_ack_retries_stable_event_without_running_command_again(inventory, pipeline, monkeypatch):
    value = envelope(inventory)
    accept(pipeline, value)
    pipeline.process.once()
    original = BrokerRepository.confirm_event
    def fail(*args, **kwargs):
        raise RuntimeError("Injected DB failure after publisher confirm")
    monkeypatch.setattr(BrokerRepository, "confirm_event", fail)
    with pytest.raises(RuntimeError):
        pipeline.publish.once()
    first = pipeline.publisher.events[0]
    monkeypatch.setattr(BrokerRepository, "confirm_event", original)
    ready_again(inventory)
    assert pipeline.publish.once()
    assert pipeline.publisher.events[1].id == first.id and pipeline.publisher.events[1].payload == first.payload
    assert not pipeline.process.once()
    assert inventory.api.item(inventory.account, "item:kit")["quantity"] == 3


def test_publication_failures_are_bounded_and_manual_replay_keeps_event_id(inventory, pipeline):
    value = envelope(inventory)
    accept(pipeline, value)
    pipeline.process.once()
    with inventory.database.transaction() as session:
        row = session.scalar(select(OutboxMessage))
        row.attempts = 19
        identity, payload = row.id, row.payload
    pipeline.publisher.fail = True
    pipeline.publish.once()
    assert not pipeline.publish.once()
    with inventory.database.transaction() as session:
        row = session.get(OutboxMessage, identity)
        assert row.failed_at and row.last_error == BrokerCode.PUBLICATION_UNAVAILABLE
    pipeline.publisher.fail = False
    assert pipeline.operations.replay(identity, outbox=True)
    pipeline.publish.once()
    assert pipeline.publisher.events[0].id == identity and pipeline.publisher.events[0].payload == payload


def test_multiple_processors_and_publishers_preserve_one_source_grant(inventory, pipeline):
    value = envelope(inventory)
    for _ in range(8):
        accept(pipeline, {**value, "messageId": str(uuid4())})
    with ThreadPoolExecutor(max_workers=4) as pool:
        list(pool.map(lambda _: pipeline.process.once(), range(8)))
        list(pool.map(lambda _: pipeline.publish.once(), range(8)))
    assert inventory.api.item(inventory.account, "item:kit")["quantity"] == 3
    assert len(pipeline.publisher.events) == 8


def test_unknown_broker_status_does_not_create_an_inbox_record(inventory, pipeline):
    with pytest.raises(ApplicationError) as error:
        pipeline.operations.get(uuid4())
    assert error.value.status_code == 404


@pytest.mark.parametrize("event", ["level.reward", "case.reward", "case.consume", "run.weapon_reward", "inventory.item.consume"])
def test_broker_dispatches_every_existing_command_with_its_original_receipt(inventory, pipeline, event):
    account = str(inventory.account)
    if event in ("level.reward", "case.reward"):
        payload = {"accountId": account, "openingId" if event == "case.reward" else "claimId": str(uuid4()),
                   "itemIndex": 0, "rewardType": "WEAPON", "rewardRef": "axe.rare", "quantity": 2}
    elif event == "run.weapon_reward":
        payload = {"accountId": account, "runId": str(uuid4()), "configurationId": "axe.rare",
                   "snapshot": inventory.api.get(inventory.account, selected_only=True)}
    else:
        kind, ref = ("CASE", "daily") if event == "case.consume" else ("ITEM", "kit")
        inventory.api.receive(uuid4(), "level.reward", 1, {"accountId": account, "claimId": str(uuid4()),
            "itemIndex": 0, "rewardType": kind, "rewardRef": ref, "quantity": 3})
        payload = ({"accountId": account, "openingId": str(uuid4()), "caseCode": ref, "quantity": 1}
                   if event == "case.consume" else {"accountId": account, "operationId": str(uuid4()),
                   "itemId": "item:" + ref, "quantity": 1})
    value = envelope(inventory, event, payload)
    accept(pipeline, value)
    assert pipeline.process.once()
    status = pipeline.operations.get(UUID(value["messageId"]))
    assert status["status"] == "APPLIED"
    assert inventory.api.receive(uuid4(), event, 1, payload)["receipt"] == status["result"]["receipt"]


def test_stale_outbox_publisher_cannot_confirm_new_lease(inventory, pipeline):
    accept(pipeline, envelope(inventory))
    pipeline.process.once()
    with inventory.api.uow() as work:
        old = work.broker.claim_event()
    ready_again(inventory)
    with inventory.api.uow() as work:
        newer = work.broker.claim_event()
        assert newer.id == old.id and newer.lease != old.lease
        assert not work.broker.confirm_event(old)
        work.broker.retry_event(old)
    with inventory.api.uow() as work:
        assert work.broker.confirm_event(newer)


def test_worker_keeps_processing_durable_commands_during_rabbit_outage(inventory, pipeline):
    from vatican_inventory.worker import run_iteration
    accept(pipeline, envelope(inventory))
    class OfflineBroker:
        def receive_once(self, receiver):
            raise RuntimeError("Rabbit offline")
    pipeline.publisher.fail = True
    worked, failed = run_iteration(OfflineBroker(), pipeline.accept, pipeline.process, pipeline.publish)
    assert worked and failed
    assert inventory.api.item(inventory.account, "item:kit")["quantity"] == 3


def test_crashed_last_allowed_attempt_transitions_to_review_after_lease_expiry(inventory, pipeline):
    value = envelope(inventory)
    accept(pipeline, value)
    with inventory.database.transaction() as session:
        session.get(BrokerInbox, UUID(value["messageId"])).attempts = 19
    with inventory.api.uow() as work:
        job = work.broker.claim_command()
        assert job.attempts == 20
    ready_again(inventory)  # Simulate process death without executing/finalizing the last claim.
    assert not pipeline.process.once()
    assert pipeline.operations.get(UUID(value["messageId"]))["status"] == InboxStatus.REVIEW


def test_quarantine_identity_cannot_collide_with_a_valid_command_event_namespace(inventory, pipeline):
    raw = b"malformed JSON"
    identity = rejection_id(raw, None, None, None, BrokerCode.INVALID_ENVELOPE)
    pipeline.accept.execute(raw)
    accept(pipeline, envelope(inventory, "unsupported.command", identity=identity))
    pipeline.process.once()
    status = pipeline.operations.get(identity)
    assert status["status"] == InboxStatus.REJECTED and len(status["events"]) == 2
    with inventory.database.transaction() as session:
        events = list(session.scalars(select(OutboxMessage)))
        assert len(events) == 3 and len({row.id for row in events}) == 3
