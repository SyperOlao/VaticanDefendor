"""Strict server event allowlist; no client identifiers beyond player/run UUIDs."""
from datetime import datetime, timezone
import hashlib
import json
import re
from uuid import UUID, uuid5

EVENT = 'run.analytics'
GIFT_EVENT = 'level-gift.analytics'
CASE_EVENT = 'case.analytics'
PURCHASE_EVENT = 'shop.purchase.analytics'
WALLET_EVENT = 'wallet.transaction.analytics'
EVENT_FIELDS = {EVENT:'runId',GIFT_EVENT:'claimId',CASE_EVENT:'openingId',PURCHASE_EVENT:'purchaseId',WALLET_EVENT:'transactionId'}
MAX_XP = 9_223_372_036_854_775_807


def integer(value, low, high):
    return type(value) is int and low <= value <= high


def parse_run(payload):
    try:
        fields={'runId','accountId','rulesetId','contentReleaseId',
                'completedAt','outcome','churchLevel','stats','progression'}
        if (not isinstance(payload,dict) or not fields <= set(payload) <= fields|{'simulationMode','matchResultSnapshot'}
                or 'simulationMode' in payload and payload['simulationMode'] not in ('LOCAL','DEDICATED')):
            raise ValueError()
        UUID(payload['runId']);UUID(payload['accountId'])
        if any(not isinstance(payload[k],str) or re.fullmatch(r'[A-Za-z0-9_.-]{1,128}',payload[k]) is None
               for k in ('rulesetId','contentReleaseId')):
            raise ValueError()
        at=datetime.fromisoformat(payload['completedAt'])
        if at.tzinfo is None or at.utcoffset() is None or at.utcoffset().total_seconds()!=0 or not 1970 <= at.year <= 2200:
            raise ValueError()
        if payload['outcome'] not in ('victory','defeat') or not integer(payload['churchLevel'],1,5):
            raise ValueError()
        stats=payload['stats']
        if not isinstance(stats,dict) or set(stats)!={'heroKills','towerKills','buildingUpgrades'}:
            raise ValueError()
        for counts in stats.values():
            if not isinstance(counts,dict) or len(counts)>256 or any(
                    not isinstance(k,str) or re.fullmatch(r'[A-Za-z0-9_.:-]{1,128}',k) is None
                    or not integer(v,0,2_147_483_647) for k,v in counts.items()):
                raise ValueError()
        if 'matchResultSnapshot' in payload:
            parse_match_snapshot(payload['matchResultSnapshot'], stats)
        progress=payload['progression']
        if progress is not None:
            if not isinstance(progress,dict) or set(progress)!={'xpGranted','totalXp','previousLevel','currentLevel','breakdown'}:
                raise ValueError()
            if (not integer(progress['xpGranted'],0,MAX_XP) or not integer(progress['totalXp'],progress['xpGranted'],MAX_XP)
                    or not integer(progress['previousLevel'],1,2_147_483_647)
                    or not integer(progress['currentLevel'],progress['previousLevel'],2_147_483_647)):
                raise ValueError()
            breakdown=progress['breakdown']
            if not isinstance(breakdown,dict) or set(breakdown)!={'heroKillXp','towerKillXp','tycoonXp','victoryXp'}:
                raise ValueError()
            if (not all(integer(v,0,MAX_XP) for v in breakdown.values())
                    or sum(breakdown.values())!=progress['xpGranted']):
                raise ValueError()
        if len(json.dumps(payload,separators=(',',':')).encode())>250_000:
            raise ValueError()
        return payload
    except (ValueError,TypeError,KeyError,AttributeError,OverflowError):
        raise ValueError('Invalid analytics run snapshot') from None



def parse_match_snapshot(snapshot, stats):
    """Telemetry extension only; awards remain owned by game_backend's pinned rules."""
    counts=('heroKills','towerKills','buildingsBuilt','buildingUpgrades')
    values=('enemiesSpawned','enemiesKilled','otherKills','heroDamageMilliUnits','towerDamageMilliUnits')
    def counters(entries):
        if not isinstance(entries,list) or len(entries)>256:raise ValueError()
        result={}
        for entry in entries:
            if (not isinstance(entry,dict) or set(entry)!={'key','count'}
                    or not isinstance(entry['key'],str) or re.fullmatch(r'[A-Za-z0-9_.:-]{1,128}',entry['key']) is None
                    or entry['key'] in result or not integer(entry['count'],0,2_147_483_647)):raise ValueError()
            result[entry['key']]=entry['count']
        return result
    def aggregate(value,extra):
        if not isinstance(value,dict) or set(value)!=set(counts+values+('resourcesProduced','resourcesSpent')+extra):raise ValueError()
        for key in counts:counters(value[key])
        if any(not integer(value[key],0,2_147_483_647 if key in values[:3] else MAX_XP) for key in values):raise ValueError()
        for key in ('resourcesProduced','resourcesSpent'):
            if (not isinstance(value[key],dict) or set(value[key])!={'wood','stone','iron'}
                    or any(not integer(n,0,MAX_XP) for n in value[key].values())):raise ValueError()
        if value['enemiesKilled']!=sum(sum(counters(value[key]).values()) for key in ('heroKills','towerKills'))+value['otherKills']:raise ValueError()
        if value['enemiesKilled']>value['enemiesSpawned']:raise ValueError()
    aggregate(snapshot,('schemaVersion','complete','durationMs','simulationTick','buildings','waves'))
    if type(snapshot['schemaVersion']) is not int or snapshot['schemaVersion']!=1 or snapshot['complete'] is not True:raise ValueError()
    if any(not integer(snapshot[key],0,MAX_XP) for key in ('durationMs','simulationTick')):raise ValueError()
    counters(snapshot['buildings'])
    waves=snapshot['waves']
    if not isinstance(waves,list) or len(waves)>1000:raise ValueError()
    seen=set()
    for wave in waves:
        aggregate(wave,('waveNumber',))
        if not integer(wave['waveNumber'],1,1000) or wave['waveNumber'] in seen:raise ValueError()
        seen.add(wave['waveNumber'])
    for key in values:
        if sum(w[key] for w in waves)!=snapshot[key]:raise ValueError()
    for key in counts:
        merged={}
        for wave in waves:
            for name,count in counters(wave[key]).items():merged[name]=merged.get(name,0)+count
        if {k:v for k,v in merged.items() if v}!={k:v for k,v in counters(snapshot[key]).items() if v}:raise ValueError()
    for key in ('resourcesProduced','resourcesSpent'):
        if any(sum(w[key][kind] for w in waves)!=snapshot[key][kind] for kind in ('wood','stone','iron')):raise ValueError()
    if any(counters(snapshot[key])!=stats[key] for key in ('heroKills','towerKills','buildingUpgrades')):raise ValueError()


def event_type(payload):
    if 'transactionId' in payload:return WALLET_EVENT
    if 'purchaseId' in payload:return PURCHASE_EVENT
    if 'claimId' in payload:return GIFT_EVENT
    if 'openingId' in payload:return CASE_EVENT
    return EVENT


def parse(payload,event_name=EVENT):
    if event_name==WALLET_EVENT:return parse_wallet(payload)
    if event_name==EVENT:return parse_run(payload)
    if event_name==PURCHASE_EVENT:return parse_purchase(payload)
    try:
        if event_name not in (GIFT_EVENT,CASE_EVENT) or not isinstance(payload,dict):raise ValueError()
        common={'accountId','rulesetId','rewardBundleId'}
        fields=(common | {'claimId','rewardedLevel','claimedAt','status'} if event_name==GIFT_EVENT else
                common | {'openingId','caseId','caseCode','paymentMode','rarity','pityBefore','pityAfter',
                          'guaranteedTriggered','entryIndex','completedAt','status'})
        if set(payload)!=fields:raise ValueError()
        for key in ('accountId','rewardBundleId',EVENT_FIELDS[event_name]):UUID(payload[key])
        if not isinstance(payload['rulesetId'],str) or re.fullmatch(r'[A-Za-z0-9_.-]{1,128}',payload['rulesetId']) is None:raise ValueError()
        at=datetime.fromisoformat(payload['claimedAt' if event_name==GIFT_EVENT else 'completedAt'])
        if at.tzinfo is None or at.utcoffset() is None or at.utcoffset().total_seconds()!=0 or not 1970<=at.year<=2200:raise ValueError()
        if event_name==GIFT_EVENT:
            if payload['status']!='CLAIMED' or not integer(payload['rewardedLevel'],1,2_147_483_647):raise ValueError()
        else:
            UUID(payload['caseId'])
            if (payload['status']!='COMPLETED' or payload['paymentMode'] not in ('INVENTORY','WALLET')
                    or not isinstance(payload['caseCode'],str) or re.fullmatch(r'[A-Za-z0-9_.-]{1,64}',payload['caseCode']) is None
                    or not isinstance(payload['rarity'],str) or re.fullmatch(r'[A-Za-z0-9_.-]{1,32}',payload['rarity']) is None
                    or any(not integer(payload[k],0,2_147_483_647) for k in ('pityBefore','pityAfter','entryIndex'))
                    or payload['pityAfter'] not in (0,payload['pityBefore']+1)
                    or type(payload['guaranteedTriggered']) is not bool
                    or payload['guaranteedTriggered'] and payload['pityAfter']!=0):raise ValueError()
        return payload
    except (ValueError,TypeError,KeyError,AttributeError,OverflowError):
        raise ValueError('Invalid analytics business snapshot') from None


def digest(payload,event_name=None):
    selected=event_name or event_type(payload)
    return hashlib.sha256(json.dumps([selected,1,payload],sort_keys=True,separators=(',',':')).encode()).digest()


def event_names(payload,event_name=None):
    selected=event_name or event_type(payload)
    if selected==WALLET_EVENT:return ['wallet.economy_transaction']
    if selected==PURCHASE_EVENT:return ['shop.purchase_confirmed']
    if selected==GIFT_EVENT:return ['level_gift.claimed']
    if selected==CASE_EVENT:return ['case.opened']
    names=['run.completed']
    if payload['progression'] is not None:
        names.append('xp.granted')
        if payload['progression']['currentLevel']>payload['progression']['previousLevel']:
            names.append('player.level_up')
    return names


def event_ids(payload,event_name=None):
    selected=event_name or event_type(payload)
    source=UUID(payload[EVENT_FIELDS[selected]])
    return [str(uuid5(source,'analytics.'+name+'.v1')) for name in event_names(payload,selected)]


def receipt(payload,event_name=None):
    selected=event_name or event_type(payload);field=EVENT_FIELDS[selected]
    return {field:payload[field],'accountId':payload['accountId'],'payloadHash':digest(payload,selected).hex(),
            'eventIds':event_ids(payload,selected)}


def parse_purchase(payload):
    try:
        fields={'purchaseId','accountId','operationId','offerId','catalogVersion','currencyId','chargedAmount',
                'weaponConfigurationId','weaponInstanceIds','status','completedAt'}
        if not isinstance(payload,dict) or set(payload)!=fields:raise ValueError()
        account,operation=UUID(payload['accountId']),UUID(payload['operationId'])
        if UUID(payload['purchaseId'])!=uuid5(account,'shop.purchase:'+str(operation)):raise ValueError()
        if (payload['status']!='Completed' or payload['currencyId']!='PRESTIGE'
                or not integer(payload['chargedAmount'],0,MAX_XP)
                or not isinstance(payload['weaponInstanceIds'],list) or len(payload['weaponInstanceIds'])!=1
                or not isinstance(payload['weaponInstanceIds'][0],str) or UUID(payload['weaponInstanceIds'][0]).int==0):raise ValueError()
        for key,pattern in [('offerId',r'[a-z0-9_.-]{1,64}'),('catalogVersion',r'[A-Za-z0-9_.-]{1,64}'),
                            ('weaponConfigurationId',r'[A-Za-z0-9_.-]{1,80}')]:
            if not isinstance(payload[key],str) or re.fullmatch(pattern,payload[key]) is None:raise ValueError()
        at=datetime.fromisoformat(payload['completedAt'])
        if at.tzinfo is None or at.utcoffset() is None or at.utcoffset().total_seconds()!=0 or not 1970<=at.year<=2200:raise ValueError()
        return payload
    except (ValueError,TypeError,KeyError,AttributeError,OverflowError):
        raise ValueError('Invalid confirmed shop purchase snapshot') from None


def parse_wallet(payload):
    try:
        fields={'transactionId','accountId','currencyId','delta','balanceAfter','reason','sourceType','sourceId',
                'itemIndex','occurredAt'}
        if not isinstance(payload,dict) or set(payload)!=fields:raise ValueError()
        for key in ('transactionId','accountId','sourceId'):UUID(payload[key])
        if (not integer(payload['delta'],-MAX_XP-1,MAX_XP) or payload['delta']==0
                or not integer(payload['balanceAfter'],0,MAX_XP)
                or not 0<=payload['balanceAfter']-payload['delta']<=MAX_XP
                or not integer(payload['itemIndex'],0,2_147_483_647)):
            raise ValueError()
        for key,size in (('currencyId',32),('sourceType',32)):
            if not isinstance(payload[key],str) or re.fullmatch(r'[A-Za-z0-9_.:-]{1,'+str(size)+'}',payload[key]) is None:
                raise ValueError()
        if not isinstance(payload['reason'],str) or not 1<=len(payload['reason'])<=64:raise ValueError()
        at=datetime.fromisoformat(payload['occurredAt'])
        if at.tzinfo is None or at.utcoffset() is None or at.utcoffset().total_seconds()!=0 or not 1970<=at.year<=2200:
            raise ValueError()
        return payload
    except (ValueError,TypeError,KeyError,AttributeError,OverflowError):
        raise ValueError('Invalid wallet ledger snapshot') from None
