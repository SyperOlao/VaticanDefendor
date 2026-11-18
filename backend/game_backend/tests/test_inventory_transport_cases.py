"""Real gift/case settlement through RabbitMQ commands and gRPC receipts."""
from datetime import datetime, timedelta, timezone
from uuid import UUID
from sqlalchemy import select

from .test_cases import case_game, gift_game, xp_game, open_case, get, items, CaseDelivery
from .test_level_rewards_lifecycle import claim
from backend.tests.test_inventory_transport import rabbit, serve_inventory, publisher, accept_and_process
from vatican_backend.application.messaging import DeliverRewards
from vatican_backend.integrations.inventory_rabbit import InventoryRabbitDelivery
from vatican_backend.persistence.postgres.tables import OutboxMessage


def settle(w, rabbit, done):
    with serve_inventory(w['inventory'], w['inventory_db']) as rpc:
        delivery = InventoryRabbitDelivery(publisher(rabbit), rpc.reader, CaseDelivery(w))
        worker = DeliverRewards(w['use_case'].uow, delivery, w.get('cases'))
        for _ in range(80):
            with w['game'].transaction() as session:
                for row in session.scalars(select(OutboxMessage).where(OutboxMessage.published_at.is_(None))):
                    row.available_at = datetime.now(timezone.utc) - timedelta(seconds=1)
            worker.once()
            accept_and_process(w['inventory'], rabbit)
            if done():return
        raise AssertionError('Settlement did not finish')


def test_level_gift_finishes_only_after_mq_grants_are_confirmed_over_grpc(gift_game, rabbit):
    w = gift_game
    assert claim(w)['status'] == 'PENDING'
    settle(w, rabbit, lambda: w['use_case'].get(w['account'], w['claim'])['status'] == 'CLAIMED')
    final = w['use_case'].get(w['account'], w['claim'])
    assert all(line['status'] == 'COMPLETED' for line in final['lines'])
    assert len(w['inventory'].get(w['account'])['Weapons']) == w['weapons_before'] + 2
    assert items(w)['item:repair-kit'] == 3 and items(w)['case:level-case'] == 1


def test_case_debit_draw_and_reward_settle_through_mq_and_grpc(case_game, rabbit):
    w = case_game
    opening = open_case(w)
    assert opening['status'] == 'FUNDING'
    settle(w, rabbit, lambda: get(w, opening['openingId'])['status'] == 'COMPLETED')
    final = get(w, opening['openingId'])
    assert final['result']['pityAfter'] == 1
    assert items(w)['case:level-case'] == 0 and items(w)['case:bonus-case'] == 1
    assert all(line['status'] == 'COMPLETED' for line in final['lines'])
    count = len(w['inventory'].get(w['account'])['Weapons'])
    repeated = w['cases'].open(w['account'], w['case'], UUID(opening['requestKey']), 'INVENTORY')
    assert repeated['replayed'] and repeated['result'] == final['result']
    assert len(w['inventory'].get(w['account'])['Weapons']) == count
