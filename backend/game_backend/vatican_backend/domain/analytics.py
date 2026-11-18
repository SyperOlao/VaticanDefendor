"""Immutable server-only analytics envelope and projection acknowledgement."""
import hashlib
import json
from enum import StrEnum
from uuid import uuid5

ANALYTICS_EVENT='run.analytics'
GIFT_ANALYTICS_EVENT='level-gift.analytics'
CASE_ANALYTICS_EVENT='case.analytics'
ANALYTICS_EVENTS=(ANALYTICS_EVENT,GIFT_ANALYTICS_EVENT,CASE_ANALYTICS_EVENT)


class AnalyticsStatus(StrEnum):
    SKIPPED='SKIPPED'
    PENDING='PENDING'
    COMPLETED='COMPLETED'
    REVIEW='REVIEW'


def analytics_message_id(source_id,event_name=ANALYTICS_EVENT):
    namespace={ANALYTICS_EVENT:'analytics.run.v1',GIFT_ANALYTICS_EVENT:'analytics.level-gift.v1',CASE_ANALYTICS_EVENT:'analytics.case.v1'}
    return uuid5(source_id,namespace[event_name])


def payload_for(run,result,stats,progression,completed_at):
    progress=None if progression is None else {key:progression[key] for key in (
        'xpGranted','totalXp','previousLevel','currentLevel','breakdown')}
    payload={'runId':str(run.id),'accountId':str(run.account_id),'rulesetId':run.ruleset_id,
            'contentReleaseId':run.content_release_id,'completedAt':completed_at.isoformat(),
            'outcome':result.outcome,'churchLevel':result.church_level,'stats':stats,'progression':progress}
    if getattr(result, 'statistics', None) is not None:
        from .match_results import validate_snapshot
        payload['matchResultSnapshot'] = validate_snapshot(result.statistics)[0]
    # Preserve historical dedicated v1 payloads/hashes, including queued retries.
    if run.simulation_mode=='LOCAL':payload['simulationMode']='LOCAL'
    return payload


def expected_receipt(payload,event_name=ANALYTICS_EVENT):
    from uuid import UUID
    field={ANALYTICS_EVENT:'runId',GIFT_ANALYTICS_EVENT:'claimId',CASE_ANALYTICS_EVENT:'openingId'}[event_name]
    run=UUID(payload[field])
    if event_name!=ANALYTICS_EVENT:
        name='level_gift.claimed' if event_name==GIFT_ANALYTICS_EVENT else 'case.opened'
        digest=hashlib.sha256(json.dumps([event_name,1,payload],sort_keys=True,separators=(',',':')).encode()).hexdigest()
        return {field:payload[field],'accountId':payload['accountId'],'payloadHash':digest,
                'eventIds':[str(uuid5(run,'analytics.'+name+'.v1'))]}
    names=['run.completed']
    if payload['progression'] is not None:
        names.append('xp.granted')
        if payload['progression']['currentLevel']>payload['progression']['previousLevel']:names.append('player.level_up')
    digest=hashlib.sha256(json.dumps([ANALYTICS_EVENT,1,payload],sort_keys=True,separators=(',',':')).encode()).hexdigest()
    return {'runId':payload['runId'],'accountId':payload['accountId'],'payloadHash':digest,
            'eventIds':[str(uuid5(run,'analytics.'+name+'.v1')) for name in names]}


def validate_analytics_receipt(payload,receipt,event_name=ANALYTICS_EVENT):
    if not isinstance(receipt,dict) or receipt!=expected_receipt(payload,event_name):
        raise ValueError('Invalid analytics acknowledgement')
