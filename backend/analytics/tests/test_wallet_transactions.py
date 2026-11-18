from contextlib import contextmanager
from datetime import datetime, timedelta, timezone
import os
from uuid import UUID, uuid4

import pytest

from vatican_analytics.application.errors import ApplicationError
from vatican_analytics.application.use_cases import ProjectEvents
from vatican_analytics.domain.events import WALLET_EVENT, receipt, event_ids
from vatican_analytics.domain.projection import rows
from vatican_analytics.persistence.postgres.tables import AnalyticsInbox


def payload():
    return {"transactionId": str(uuid4()), "accountId": str(uuid4()), "currencyId": "PRESTIGE",
            "delta": -25, "balanceAfter": 475, "reason": "PURCHASE", "sourceType": "PURCHASE",
            "sourceId": str(uuid4()), "itemIndex": 0, "occurredAt": datetime.now(timezone.utc).isoformat()}


@pytest.mark.parametrize("patch", [{"delta": 0}, {"delta": True}, {"delta": "25"}, {"delta": 2**63},
    {"balanceAfter": -1}, {"balanceAfter": True}, {"delta": 500, "balanceAfter": 0},
    {"itemIndex": -1}, {"itemIndex": True}, {"accountId": "invalid"}, {"transactionId": "invalid"},
    {"occurredAt": "2026-11-17T12:00:00"}, {"currencyId": "a" * 33}, {"token": "private"}])
def test_invalid_wallet_events_do_not_enter_inbox(analytics_actor, patch):
    with pytest.raises(ApplicationError) as error:
        analytics_actor.api.receive(uuid4(), WALLET_EVENT, 1, {**payload(), **patch})
    assert error.value.status_code == 422


def test_transaction_source_is_immutable_across_transport_aliases(analytics_actor):
    actor = analytics_actor
    body, message = payload(), uuid4()
    assert actor.api.receive(message, WALLET_EVENT, 1, body)["status"] == "PENDING"
    assert actor.worker.once()
    saved = actor.api.receive(message, WALLET_EVENT, 1, body)["receipt"]
    assert saved == receipt(body, WALLET_EVENT)
    assert actor.api.receive(uuid4(), WALLET_EVENT, 1, body)["receipt"] == saved
    for patch in ({"accountId": str(uuid4())}, {"delta": -30}, {"balanceAfter": 470},
                  {"currencyId": "GEMS"}, {"sourceId": str(uuid4())}):
        with pytest.raises(ApplicationError) as error:
            actor.api.receive(uuid4(), WALLET_EVENT, 1, {**body, **patch})
        assert error.value.status_code == 409
    assert len(actor.projection.tables["fact_economy_transactions"]) == 1
    facts = rows(body)
    assert facts["raw_game_events"][0]["source"] == "wallet"
    assert facts["fact_economy_transactions"][0]["amount_delta"] == -25
    assert facts["raw_game_events"][0]["run_id"] is None
    assert rows({**body, "sourceType": "RUN_REWARD"})["raw_game_events"][0]["run_id"] == UUID(body["sourceId"])


@pytest.mark.parametrize("fail_after", [1, 2])
def test_live_partial_clickhouse_projection_replay_counts_money_once(analytics_actor, fail_after):
    url = os.getenv("VD_TEST_ANALYTICS_DATABASE_URL")
    if not url:
        pytest.skip("Set VD_TEST_ANALYTICS_DATABASE_URL to disposable ClickHouse")
    from vatican_analytics.init_db import init_db
    from vatican_analytics.persistence.clickhouse import Database
    from vatican_analytics.persistence.clickhouse.repositories.projection import ClickHouseProjection
    init_db(url)
    warehouse = Database(url)
    body = payload()
    source, message = UUID(body["transactionId"]), uuid4()
    actor = analytics_actor
    class FaultyWarehouse:
        class Engine:
            @contextmanager
            def connect(self):
                with warehouse.engine.connect() as connection:
                    class Connection:
                        count = 0
                        def execute(self, statement, values):
                            connection.execute(statement, values)
                            self.count += 1
                            if self.count == fail_after:
                                raise RuntimeError("lost warehouse ack")
                    yield Connection()
        engine = Engine()
    try:
        actor.api.receive(message, WALLET_EVENT, 1, body)
        with pytest.raises(RuntimeError):
            ProjectEvents(actor.uow, ClickHouseProjection(FaultyWarehouse())).once()
        assert actor.api.receive(message, WALLET_EVENT, 1, body)["status"] == "PENDING"
        with actor.database.transaction() as session:
            session.get(AnalyticsInbox, (WALLET_EVENT, source)).available_at = datetime.now(timezone.utc) - timedelta(seconds=1)
        assert ProjectEvents(actor.uow, ClickHouseProjection(warehouse)).once()
        assert actor.api.receive(message, WALLET_EVENT, 1, body)["status"] == "APPLIED"
        with warehouse.engine.connect() as connection:
            assert connection.exec_driver_sql("SELECT count(),sum(amount_delta) FROM fact_economy_transactions FINAL "
                "WHERE transaction_id=%(id)s", {"id": str(source)}).one() == (1, -25)
            assert connection.exec_driver_sql("SELECT count() FROM raw_game_events FINAL WHERE event_id=%(id)s",
                {"id": event_ids(body, WALLET_EVENT)[0]}).scalar() == 1
    finally:
        warehouse.close()
