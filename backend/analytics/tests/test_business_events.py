from concurrent.futures import ThreadPoolExecutor
from copy import deepcopy
from datetime import datetime, timedelta, timezone
from uuid import UUID, uuid4
import pytest
from sqlalchemy import func, select, text
from vatican_analytics.application.errors import ApplicationError
from vatican_analytics.domain.events import EVENT, GIFT_EVENT, CASE_EVENT, parse, digest, receipt, event_ids
from vatican_analytics.domain.projection import rows
from vatican_analytics.persistence.postgres.tables import AnalyticsInbox, ReceivedMessage
from vatican_analytics.migrate import migrate
from vatican_analytics.application.use_cases import AnalyticsUseCases
from vatican_analytics.persistence.postgres.unit_of_work import PostgresUnitOfWorkFactory
from .test_ingestion import payload as run_payload


def gift_payload(identity=None):
    return {'claimId':str(identity or uuid4()),'accountId':str(uuid4()),'rulesetId':'xp-v1','rewardedLevel':2,
        'rewardBundleId':str(uuid4()),'status':'CLAIMED','claimedAt':datetime.now(timezone.utc).isoformat()}


def case_payload(identity=None):
    return {'openingId':str(identity or uuid4()),'accountId':str(uuid4()),'caseId':str(uuid4()),'caseCode':'level.local',
        'rulesetId':'case-v1','paymentMode':'INVENTORY','rewardBundleId':str(uuid4()),'rarity':'RARE','pityBefore':2,
        'pityAfter':0,'guaranteedTriggered':True,'entryIndex':1,'status':'COMPLETED','completedAt':datetime.now(timezone.utc).isoformat()}


@pytest.mark.parametrize('event,factory,field,table',[(GIFT_EVENT,gift_payload,'claimId','fact_level_gift_claims'),
    (CASE_EVENT,case_payload,'openingId','fact_case_settlements')])
def test_business_source_is_once_only_across_aliases_and_owner_payload_conflicts(analytics_actor,event,factory,field,table):
    actor=analytics_actor;body=factory();identity=uuid4()
    assert actor.api.receive(identity,event,1,body)['status']=='PENDING'
    actor.worker.once()
    saved=actor.api.receive(identity,event,1,body)['receipt']
    assert saved==receipt(body,event) and len(saved['eventIds'])==1
    assert actor.api.receive(uuid4(),event,1,body)['receipt']==saved
    with pytest.raises(ApplicationError) as error:actor.api.receive(uuid4(),event,1,{**body,'accountId':str(uuid4())})
    assert error.value.status_code==409
    assert not actor.worker.once() and len(actor.projection.tables[table])==1
    raw=actor.projection.tables['raw_game_events'][UUID(saved['eventIds'][0])]
    assert raw['run_id'] is None and raw['session_id'] is None


@pytest.mark.parametrize('event,factory,patch',[(GIFT_EVENT,gift_payload,{'status':'AVAILABLE'}),
    (GIFT_EVENT,gift_payload,{'rewardedLevel':True}),(GIFT_EVENT,gift_payload,{'token':'private'}),
    (GIFT_EVENT,gift_payload,{'claimedAt':'2026-11-17T10:00:00'}),
    (CASE_EVENT,case_payload,{'status':'PENDING'}),(CASE_EVENT,case_payload,{'seed':'private'}),
    (CASE_EVENT,case_payload,{'guaranteedTriggered':1}),(CASE_EVENT,case_payload,{'pityAfter':1}),
    (CASE_EVENT,case_payload,{'paymentMode':'FREE'}),(CASE_EVENT,case_payload,{'entryIndex':-1})])
def test_business_allowlist_and_terminal_state_are_validated_before_persistence(analytics_actor,event,factory,patch):
    with pytest.raises(ApplicationError) as error:analytics_actor.api.receive(uuid4(),event,1,{**factory(),**patch})
    assert error.value.status_code==422
    with analytics_actor.database.transaction() as session:assert session.scalar(select(func.count()).select_from(AnalyticsInbox))==0


def test_same_uuid_in_run_gift_and_case_namespaces_remains_three_independent_sources(analytics_actor):
    actor=analytics_actor;identity=uuid4()
    sources=[(EVENT,run_payload(runId=str(identity))),(GIFT_EVENT,gift_payload(identity)),(CASE_EVENT,case_payload(identity))]
    first_id=uuid4()
    actor.api.receive(first_id,sources[0][0],1,sources[0][1])
    with pytest.raises(ApplicationError) as error:actor.api.receive(first_id,sources[1][0],1,sources[1][1])
    assert error.value.status_code==409
    for event,body in sources[1:]:actor.api.receive(uuid4(),event,1,body)
    for _ in sources:assert actor.worker.once()
    assert not actor.worker.once()
    assert len({str(event_ids(body,event)[0]) for event,body in sources})==3
    with actor.database.transaction() as session:
        assert session.scalar(select(func.count()).select_from(AnalyticsInbox))==3
        assert session.scalar(select(func.count()).select_from(ReceivedMessage))==3


def test_parallel_business_aliases_and_sources_do_not_duplicate_projection(analytics_actor):
    actor=analytics_actor;identity=uuid4();bodies=[(GIFT_EVENT,gift_payload(identity)),(CASE_EVENT,case_payload(identity))]
    with ThreadPoolExecutor(max_workers=6) as pool:
        list(pool.map(lambda source:actor.api.receive(uuid4(),source[0],1,source[1]),bodies*6))
        list(pool.map(lambda _:actor.worker.once(),range(6)))
    assert actor.projection.calls==2
    assert len(actor.projection.tables['fact_level_gift_claims'])==len(actor.projection.tables['fact_case_settlements'])==1
