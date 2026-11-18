from ....application.catalog import RewardDestination, RewardEvent, RewardStatus
from datetime import datetime, timedelta, timezone
from uuid import uuid4, uuid5
from secrets import choice
from sqlalchemy import select, update
from ..tables import OutboxMessage, WeaponConfiguration, LevelRewardGrant, CaseOpening, CaseRewardGrant
from .level_rewards import LevelRewardsRepository
from .cases import CasesRepository
from ....domain.cases import OpeningStatus, CASE_REWARD, funding_id, reward_id
from ....domain.analytics import ANALYTICS_EVENT, AnalyticsStatus, analytics_message_id
from ....domain.rating import RATING_EVENT, RatingStatus, rating_message_id
from ....domain.level_rewards import LEVEL_REWARD_EVENT, RewardType, GrantStatus, message_id as reward_message_id


def utcnow():
    return datetime.now(timezone.utc)


class MessagingRepository:
    def __init__(self, session):
        self.session = session

    def enqueue_rewards(self, run, reward, weapon_configuration_id=None):
        if reward:
            self.session.add(OutboxMessage(id=uuid5(run.id, "reward.wallet"), destination=RewardDestination.WALLET,
                event_name=RewardEvent.CURRENCY, schema_version=1, aggregate_id=run.id,
                payload={"accountId": str(run.account_id), "runId": str(run.id), "amount": reward}))
        configurations = ([weapon_configuration_id] if weapon_configuration_id is not None else
                          list(self.session.scalars(select(WeaponConfiguration.configuration_id))))
        self.session.add(OutboxMessage(id=uuid5(run.id, "reward.inventory"), destination=RewardDestination.INVENTORY,
            event_name=RewardEvent.WEAPON, schema_version=1, aggregate_id=run.id,
            payload={"accountId": str(run.account_id), "runId": str(run.id), "configurationId": choice(configurations), "snapshot": run.weapon_snapshot}))

    def enqueue_rating(self, run, board_code, points):
        self.session.add(OutboxMessage(id=rating_message_id(run.id),destination=RewardDestination.RATING,
            event_name=RATING_EVENT,schema_version=1,aggregate_id=run.id,payload={
                'accountId':str(run.account_id),'runId':str(run.id),'boardCode':board_code,
                'rulesetId':run.ruleset_id,'contentReleaseId':run.content_release_id,'points':points}))

    def rating_delivery(self, run_id):
        row = self.session.get(OutboxMessage,rating_message_id(run_id))
        if row is None:
            return {'status':RatingStatus.SKIPPED,'messageId':None,'receipt':None}
        status = RatingStatus.COMPLETED if row.published_at else RatingStatus.REVIEW if row.failed_at else RatingStatus.PENDING
        return {'status':status,'messageId':str(row.id),'boardCode':row.payload['boardCode'],'points':row.payload['points'],
                'attempts':row.attempts,'lastError':row.last_error,'receipt':row.receipt_json,
                'brokerPublishedAt':row.broker_published_at.isoformat() if row.broker_published_at else None,
                'resultGeneration':row.broker_result_generation}

    def enqueue_analytics(self, run_id, payload):
        self.session.add(OutboxMessage(id=analytics_message_id(run_id),destination=RewardDestination.ANALYTICS,
            event_name=ANALYTICS_EVENT,schema_version=1,aggregate_id=run_id,payload=payload))

    def analytics_delivery(self, source_id, event_name=ANALYTICS_EVENT):
        from .analytics import delivery
        return delivery(self.session,source_id,event_name)

    def reward_status(self, run_id):
        rows = list(self.session.scalars(select(OutboxMessage).where(OutboxMessage.aggregate_id == run_id,
            OutboxMessage.destination.in_((RewardDestination.WALLET,RewardDestination.INVENTORY)))))
        if any(row.failed_at is not None for row in rows):
            return RewardStatus.FAILED
        return RewardStatus.PENDING if any(row.published_at is None for row in rows) else RewardStatus.COMPLETED

    def claim(self):
        row = self.session.scalar(select(OutboxMessage).where(OutboxMessage.published_at.is_(None),
            OutboxMessage.failed_at.is_(None), OutboxMessage.available_at <= utcnow())
            .order_by(OutboxMessage.created_at, OutboxMessage.id).limit(1).with_for_update(skip_locked=True))
        if row is None:
            return None
        if row.attempts >= 20:
            row.failed_at, row.lease_token, row.last_error = utcnow(), None, 'DeliveryUnavailable'
            self._update_level_grant(row.id, GrantStatus.FAILED)
            self._update_case(row.id, GrantStatus.FAILED)
            return None
        row.lease_token = uuid4()
        row.available_at = utcnow() + timedelta(seconds=30)
        row.attempts += 1
        return {"id": row.id, "destination": row.destination, "event_name": row.event_name,
                "schema_version": row.schema_version, "payload": row.payload, "lease": row.lease_token}

    def dispatched(self, message_id, lease):
        row = self.session.get(OutboxMessage, message_id, with_for_update=True)
        if row is None or row.lease_token != lease or row.published_at is not None or row.failed_at is not None:
            return False
        row.broker_published_at = row.broker_published_at or utcnow()
        row.lease_token, row.last_error = None, None
        row.available_at = utcnow() + timedelta(seconds=60)
        if row.attempts >= 20:
            row.failed_at, row.last_error = utcnow(), 'RatingReceiptUnavailable'
        return True

    def enqueue_level_reward(self, account_id, claim_id, line):
        destination = RewardDestination.WALLET if line.reward_type == RewardType.CURRENCY else RewardDestination.INVENTORY
        identity = reward_message_id(claim_id, line.item_index)
        self.session.add(OutboxMessage(id=identity, destination=destination, event_name=LEVEL_REWARD_EVENT,
            schema_version=1, aggregate_id=claim_id, payload=line.payload(account_id, claim_id)))
        self.session.flush()
        return identity

    def enqueue_case(self, opening_id, destination, event, payload, item_index=None):
        identity = funding_id(opening_id) if item_index is None else reward_id(opening_id,item_index)
        self.session.add(OutboxMessage(id=identity,destination=destination,event_name=event,schema_version=1,
            aggregate_id=opening_id,payload=payload))
        self.session.flush()
        return identity

    def _update_case(self, message_id, status, receipt=None):
        grant = self.session.scalar(select(CaseRewardGrant).where(CaseRewardGrant.message_id==message_id).with_for_update())
        if grant:
            grant.status = status
            if status == GrantStatus.COMPLETED:
                grant.receipt_json, grant.completed_at = receipt, utcnow()
            CasesRepository(self.session).finalize(grant.opening_id,utcnow())
            return
        if status == GrantStatus.COMPLETED:
            return  # Funding settlement locks profile before opening in the application layer.
        opening = self.session.scalar(select(CaseOpening).where(CaseOpening.payment_message_id==message_id).with_for_update())
        if opening and not opening.result and opening.status not in (OpeningStatus.REJECTED,OpeningStatus.COMPLETED):
            opening.status = OpeningStatus.REVIEW if status==GrantStatus.FAILED else OpeningStatus.FUNDING

    def _update_level_grant(self, message_id, status, receipt=None):
        grant = self.session.scalar(select(LevelRewardGrant).where(LevelRewardGrant.message_id == message_id).with_for_update())
        if grant is None:
            return
        grant.status = status
        if status == GrantStatus.COMPLETED:
            grant.receipt_json = receipt
            grant.completed_at = utcnow()
        LevelRewardsRepository(self.session).finalize(grant.claim_id, utcnow())

    def confirm(self, message_id, lease, receipt=None):
        changed = self.session.scalar(update(OutboxMessage).where(OutboxMessage.id == message_id,
            OutboxMessage.lease_token == lease, OutboxMessage.published_at.is_(None))
            .values(published_at=utcnow(), lease_token=None, last_error=None, receipt_json=receipt).returning(OutboxMessage.id))
        if changed is not None:
            self._update_level_grant(message_id, GrantStatus.COMPLETED, receipt)
            self._update_case(message_id, GrantStatus.COMPLETED, receipt)
        return changed is not None

    def retry(self, message_id, lease, permanent=False):
        row = self.session.get(OutboxMessage, message_id, with_for_update=True)
        if row is None or row.lease_token != lease or row.published_at is not None:
            return
        row.lease_token = None
        row.last_error = "DeliveryRejected" if permanent else "DeliveryUnavailable"
        if permanent or row.attempts >= 20:
            row.failed_at = utcnow()
            self._update_level_grant(message_id, GrantStatus.FAILED)
            self._update_case(message_id, GrantStatus.FAILED)
        else:
            row.available_at = utcnow() + timedelta(seconds=min(300, 2 ** min(row.attempts, 8)))


    def requeue(self, message_id):
        row = self.session.get(OutboxMessage, message_id, with_for_update=True)
        if row is None or row.published_at is not None or row.failed_at is None:
            return False
        row.failed_at = None
        row.lease_token = None
        row.attempts = 0
        row.last_error = None
        row.available_at = utcnow()
        self._update_level_grant(message_id, GrantStatus.PENDING)
        self._update_case(message_id, GrantStatus.PENDING)
        return True
