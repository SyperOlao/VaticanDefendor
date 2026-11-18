from datetime import datetime
import json
from uuid import UUID, uuid5
from .events import parse, event_ids, event_names, event_type, EVENT, GIFT_EVENT, CASE_EVENT, PURCHASE_EVENT, WALLET_EVENT


def rows(payload):
    selected=event_type(payload)
    parse(payload,selected)
    if selected==WALLET_EVENT:return wallet_rows(payload)
    if selected!=EVENT:return business_rows(payload,selected)
    run,player=UUID(payload['runId']),UUID(payload['accountId'])
    at=datetime.fromisoformat(payload['completedAt'])
    progress=payload['progression'];stats=payload['stats']
    mode=payload.get('simulationMode','DEDICATED')
    hero,tower,buildings=(sum(stats[k].values()) for k in ('heroKills','towerKills','buildingUpgrades'))
    names,ids=event_names(payload),event_ids(payload)
    result={'raw_game_events':[{'event_id':UUID(identity),'player_id':player,'session_id':None,'run_id':run,
        'event_name':name,'source':'game_backend','schema_version':1,'app_version':None,'occurred_at':at,
        'payload_json':json.dumps(payload,sort_keys=True,separators=(',',':'))} for name,identity in zip(names,ids)],
        'fact_run_settlements':[{'run_id':run,'player_id':player,'ruleset_id':payload['rulesetId'],
            'content_release_id':payload['contentReleaseId'],'simulation_mode':mode,
            'outcome':payload['outcome'],'church_level':payload['churchLevel'],
            'hero_kills':hero,'tower_kills':tower,'building_upgrades':buildings,
            'xp_granted':progress['xpGranted'] if progress else None,'total_xp':progress['totalXp'] if progress else None,
            'level_after':progress['currentLevel'] if progress else None,'occurred_at':at}]}
    if progress is not None:
        breakdown=progress['breakdown']
        result['fact_xp_transactions']=[{'event_id':UUID(ids[1]),'player_id':player,'occurred_at':at,
            'run_id':run,'source_type':mode+'_RUN','xp_delta':progress['xpGranted'],'level_after':progress['currentLevel'],
            'xp_transaction_id':uuid5(run,'player.xp'),'ruleset_id':payload['rulesetId'],
            'hero_mob_kills':hero if hero<=2**32-1 else None,'tower_mob_kills':tower if tower<=2**32-1 else None,
            'building_upgrades':buildings if buildings<=2**32-1 else None,'hero_kill_xp':breakdown['heroKillXp'],
            'tower_kill_xp':breakdown['towerKillXp'],'tycoon_upgrade_xp':breakdown['tycoonXp'],'victory_xp':breakdown['victoryXp']}]
        if progress['currentLevel']>progress['previousLevel']:
            result['fact_player_level_ups']=[{'event_id':UUID(ids[2]),'player_id':player,'occurred_at':at,'run_id':run,
                'old_level':progress['previousLevel'],'new_level':progress['currentLevel'],'total_xp':progress['totalXp']}]
    return result


def wallet_rows(payload):
    player=UUID(payload['accountId']);identity=UUID(event_ids(payload,WALLET_EVENT)[0])
    at=datetime.fromisoformat(payload['occurredAt'])
    run=UUID(payload['sourceId']) if payload['sourceType']=='RUN_REWARD' else None
    return {'raw_game_events':[{'event_id':identity,'player_id':player,'run_id':run,'session_id':None,
        'event_name':'wallet.economy_transaction','source':'wallet','schema_version':1,'app_version':None,
        'occurred_at':at,'payload_json':json.dumps(payload,sort_keys=True,separators=(',',':'))}],
        'fact_economy_transactions':[{'event_id':identity,'player_id':player,'occurred_at':at,
            'transaction_id':UUID(payload['transactionId']),'currency_code':payload['currencyId'],
            'amount_delta':payload['delta'],'balance_after':payload['balanceAfter'],'reason':payload['reason']}]}


def business_rows(payload,event_name):
    player=UUID(payload['accountId']);identity=UUID(event_ids(payload,event_name)[0])
    at=datetime.fromisoformat(payload['claimedAt' if event_name==GIFT_EVENT else 'completedAt'])
    result={'raw_game_events':[{'event_id':identity,'player_id':player,'run_id':None,'session_id':None,
        'event_name':event_names(payload,event_name)[0],'source':'shop' if event_name==PURCHASE_EVENT else 'game_backend','schema_version':1,'app_version':None,
        'occurred_at':at,'payload_json':json.dumps(payload,sort_keys=True,separators=(',',':'))}]}
    if event_name==PURCHASE_EVENT:
        result['fact_shop_purchases']=[{'purchase_id':UUID(payload['purchaseId']),'operation_id':UUID(payload['operationId']),
            'player_id':player,'offer_id':payload['offerId'],'catalog_version':payload['catalogVersion'],
            'currency_code':payload['currencyId'],'charged_amount':payload['chargedAmount'],
            'weapon_configuration_id':payload['weaponConfigurationId'],'weapon_instance_id':UUID(payload['weaponInstanceIds'][0]),
            'completed_at':at}]
    elif event_name==GIFT_EVENT:
        result['fact_level_gift_claims']=[{'claim_id':UUID(payload['claimId']),'player_id':player,
            'rewarded_level':payload['rewardedLevel'],'reward_bundle_id':UUID(payload['rewardBundleId']),
            'claim_status':'CLAIMED','claimed_at':at}]
    else:
        result['fact_case_settlements']=[{'opening_id':UUID(payload['openingId']),'player_id':player,
            'case_id':UUID(payload['caseId']),'case_code':payload['caseCode'],'ruleset_id':payload['rulesetId'],
            'reward_bundle_id':UUID(payload['rewardBundleId']),'payment_mode':payload['paymentMode'],
            'rarity':payload['rarity'],'pity_before':payload['pityBefore'],'pity_after':payload['pityAfter'],
            'guaranteed_triggered':int(payload['guaranteedTriggered']),'entry_index':payload['entryIndex'],'completed_at':at}]
    return result
