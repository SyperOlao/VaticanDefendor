"""Pinned plausibility limits for client-simulated runs; not proof of simulation."""
from copy import deepcopy
from dataclasses import dataclass
from enum import StrEnum
import hashlib
import json
import re
from uuid import UUID
from .progression import RunStats, integer, MAX_COUNT

VALIDATOR_VERSION='local.v1'
GROUPS=('heroKills','towerKills','buildingUpgrades')
IDENTIFIER=re.compile(r'[A-Za-z0-9_.:-]{1,128}')


class ValidationStatus(StrEnum):
    ACCEPTED='ACCEPTED'
    REJECTED='REJECTED'


def digest(value):
    return hashlib.sha256(json.dumps(value,sort_keys=True,separators=(',',':'),allow_nan=False).encode()).digest()


def start_digest(policy_id):return digest(['LOCAL',1,policy_id])


class LocalPolicy:
    def __init__(self,value):
        fields={'policyId','contentReleaseId','rulesetId','mapId','difficulty','submissionWindowSeconds',
            'minDurationMs','minVictoryDurationMs','maxDurationMs','clockToleranceMs','victoryChurchLevel',
            'maxEventsPerSecond','counterLimits','prestigePerChurchLevel','rewardWeaponConfigurationId'}
        if not isinstance(value,dict) or set(value)!=fields:raise ValueError('Invalid local policy fields')
        for key in ('policyId','contentReleaseId','rulesetId','mapId','difficulty','rewardWeaponConfigurationId'):
            if not isinstance(value[key],str) or IDENTIFIER.fullmatch(value[key]) is None:raise ValueError('Invalid local policy identifier')
        if len(value['difficulty'])>32 or len(value['rewardWeaponConfigurationId'])>80:raise ValueError('Invalid local policy identifier')
        ranges={'submissionWindowSeconds':(1,604800),'minDurationMs':(1,604800000),
            'minVictoryDurationMs':(1,604800000),'maxDurationMs':(1,604800000),
            'clockToleranceMs':(0,60000),'victoryChurchLevel':(2,5),
            'maxEventsPerSecond':(1,MAX_COUNT),'prestigePerChurchLevel':(0,1_000_000)}
        if any(not integer(value[k],*bounds) for k,bounds in ranges.items()):raise ValueError('Invalid local policy limits')
        if not value['minDurationMs']<=value['minVictoryDurationMs']<=value['maxDurationMs']<=value['submissionWindowSeconds']*1000:
            raise ValueError('Inconsistent local policy durations')
        limits=value['counterLimits']
        if not isinstance(limits,dict) or set(limits)!=set(GROUPS):raise ValueError('Invalid counter limits')
        for group in GROUPS:
            if not isinstance(limits[group],dict) or len(limits[group])>256:raise ValueError('Invalid counter limits')
            if any(not isinstance(k,str) or IDENTIFIER.fullmatch(k) is None or not integer(v,0,MAX_COUNT)
                   for k,v in limits[group].items()):raise ValueError('Invalid counter limit')
        self._value=deepcopy(value)

    def snapshot(self):return deepcopy(self._value)
    def __getitem__(self,key):return self._value[key] if key!='counterLimits' else deepcopy(self._value[key])


@dataclass(frozen=True)
class LocalResult:
    session_id: UUID
    policy_id: str
    content_release_id: str
    ruleset_id: str
    map_id: str
    difficulty: str
    outcome: str
    church_level: int
    duration_ms: int
    stats: RunStats

    @classmethod
    def from_snapshot(cls,value):
        fields={'schemaVersion','sessionId','policyId','contentReleaseId','rulesetId','mapId','difficulty',
                'outcome','churchLevel','durationMs','stats'}
        if not isinstance(value,dict) or set(value)!=fields or type(value['schemaVersion']) is not int or value['schemaVersion']!=1:
            raise ValueError('Unsupported local result schema')
        stats=value['stats']
        if not isinstance(stats,dict) or set(stats)!=set(GROUPS) or any(not isinstance(stats[k],dict) for k in GROUPS):
            raise ValueError('Invalid local counters')
        result=cls(UUID(value['sessionId']),value['policyId'],value['contentReleaseId'],value['rulesetId'],
            value['mapId'],value['difficulty'],value['outcome'],value['churchLevel'],value['durationMs'],
            RunStats(*(tuple(sorted(stats[k].items())) for k in GROUPS)))
        result.snapshot()
        return result

    def snapshot(self):
        if (not isinstance(self.session_id,UUID) or self.session_id.int==0 or self.outcome not in ('victory','defeat')
                or not integer(self.church_level,1,5) or not integer(self.duration_ms,0,604800000)
                or not isinstance(self.stats,RunStats)):raise ValueError('Invalid local result')
        for v in (self.policy_id,self.content_release_id,self.ruleset_id,self.map_id,self.difficulty):
            if not isinstance(v,str) or IDENTIFIER.fullmatch(v) is None:raise ValueError('Invalid result identifier')
        return {'schemaVersion':1,'sessionId':str(self.session_id),'policyId':self.policy_id,
            'contentReleaseId':self.content_release_id,'rulesetId':self.ruleset_id,'mapId':self.map_id,
            'difficulty':self.difficulty,'outcome':self.outcome,'churchLevel':self.church_level,
            'durationMs':self.duration_ms,'stats':self.stats.snapshot()}


def validate_result(policy,context,snapshot,received_at):
    reasons=[]
    if received_at>=context.submission_deadline:reasons.append('SubmissionExpired')
    if snapshot['sessionId']!=str(context.session_id):reasons.append('SessionMismatch')
    for key in ('policyId','contentReleaseId','rulesetId','mapId','difficulty'):
        if snapshot[key]!=policy[key]:reasons.append('ContextMismatch');break
    elapsed=received_at-context.started_at
    elapsed_ms=(elapsed.days*86400+elapsed.seconds)*1000+elapsed.microseconds//1000
    duration=snapshot['durationMs']
    if duration>elapsed_ms+policy['clockToleranceMs']:reasons.append('DurationExceedsServerTime')
    if not policy['minDurationMs']<=duration<=policy['maxDurationMs']:reasons.append('DurationOutsideLimits')
    victory=snapshot['outcome']=='victory'
    if victory and duration<policy['minVictoryDurationMs']:reasons.append('VictoryTooEarly')
    if (victory and snapshot['churchLevel']!=policy['victoryChurchLevel']
            or not victory and snapshot['churchLevel']>=policy['victoryChurchLevel']):reasons.append('OutcomeMismatch')
    total=0
    for group,counts in snapshot['stats'].items():
        limits=policy['counterLimits'][group]
        for key,count in counts.items():
            total+=count
            if key not in limits:reasons.append('UnknownCounterTarget')
            elif count>limits[key]:reasons.append('CounterLimitExceeded')
    if total*1000>policy['maxEventsPerSecond']*duration:reasons.append('CounterRateExceeded')
    return sorted(set(reasons))
