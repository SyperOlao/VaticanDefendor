"""Deterministic, linked synthetic business facts for visual analytics review."""
from collections import defaultdict
from datetime import datetime, timedelta, timezone
from random import Random
from uuid import UUID, uuid5

SEED_VERSION = 1
NAMESPACE = UUID('f6f07916-7b3d-4fd0-8550-81fdb6e3a7ef')
TABLES = ('fact_run_settlements', 'fact_xp_transactions', 'fact_player_level_ups',
          'fact_level_gift_claims', 'fact_case_settlements', 'fact_shop_purchases', 'raw_game_events')


def uid(key):
    return uuid5(NAMESPACE, key)


def utc_reference(value):
    # ClickHouse may return DateTime64('UTC') as a naive datetime. Normalize before
    # deriving the random seed, otherwise recovery would generate different facts.
    return value.replace(tzinfo=timezone.utc) if value.tzinfo is None else value.astimezone(timezone.utc)


def generate(reference_at):
    """No real player data. All IDs and dates reproduce exactly for a saved reference."""
    reference_at = utc_reference(reference_at)
    rng = Random('vatican-demo-v1:' + reference_at.isoformat())
    rows = {name: [] for name in TABLES}
    players = [uid(f'player:{i}') for i in range(480)]
    xp_by_player = defaultdict(int)
    pity = defaultdict(int)
    serial = 0

    def event(name, key, player, occurred_at, day_index):
        index = len(rows['raw_game_events'])
        source = 'shop' if name == 'shop.purchase_confirmed' else 'game_backend'
        lag = rng.uniform(.2, 8) if source == 'shop' else rng.uniform(.05, 3)
        if day_index == 77 and rng.random() < .45:
            lag = rng.uniform(600, 1800)  # One explicit delivery incident 12 days ago.
        if index % 257 == 0:
            lag = -2  # Clock skew has its own KPI, excluded from average lag.
        received_at = occurred_at + timedelta(seconds=lag)
        received_at = received_at.replace(microsecond=received_at.microsecond // 1000 * 1000)
        rows['raw_game_events'].append(dict(event_id=uid('event:' + name + ':' + key),
            player_id=player, occurred_at=occurred_at, received_at=received_at,
            event_name=name, source=source, schema_version=1, payload_json='{"synthetic":true}'))

    for day_index in range(90):
        day = reference_at.replace(hour=0, minute=0, second=0, microsecond=0) - timedelta(days=89-day_index)
        seconds = 86400 if day.date() < reference_at.date() else max(1, int((reference_at-day).total_seconds())-3600)
        fraction = seconds / 86400
        weekend = 1.28 if day.weekday() >= 5 else 1
        current = 0 if day_index < 30 else 1 if day_index < 65 else 2
        release = ('demo-1.0', 'demo-1.1', 'demo-1.2')[current]

        def at():
            return day + timedelta(seconds=rng.randrange(seconds), milliseconds=rng.randrange(1000))

        run_count = max(1, int((85 + day_index * .9 + rng.randrange(24)) * weekend * fraction))
        for occurred_at in sorted(at() for _ in range(run_count)):
            serial += 1
            key = f'{day.date()}:run:{serial}'
            run_id = uid(key)
            # Regular players return across days; unique-period players cannot be summed by day.
            player = rng.choice(players[:320] if rng.random() < .72 else players)
            rules = current-1 if current and rng.random() < .16 else current
            ruleset = ('demo-1.0', 'demo-1.1', 'demo-1.2')[rules]
            victory = rng.random() < (.47, .58, .68)[rules]
            hero = max(0, int(rng.gauss((18, 21, 25)[rules], 8)))
            tower = max(0, int(rng.gauss((12, 15, 19)[rules], 6)))
            upgrades = rng.randrange(0, 9) if victory else rng.randrange(0, 5)
            components = [hero*3, tower*2, upgrades*12, (1200 if serial % 170 == 0 else 80) if victory else 0]
            if not victory and serial % 31 == 0:
                components = [0, 0, 0, 0]  # Known zero XP, distinct from unavailable XP.
            gained = sum(components)
            known = rng.random() < (.70, .93, .995)[rules]
            old_level = xp_by_player[player] // 600 + 1
            xp_by_player[player] += gained
            new_level = xp_by_player[player] // 600 + 1
            rows['fact_run_settlements'].append(dict(run_id=run_id, player_id=player, occurred_at=occurred_at,
                ruleset_id=ruleset, content_release_id=release, outcome='victory' if victory else 'defeat',
                church_level=min(12, max(1, int(rng.gauss(6 if victory else 3, 1.5)))),
                hero_kills=hero, tower_kills=tower, building_upgrades=upgrades,
                xp_granted=gained if known else None, total_xp=xp_by_player[player] if known else None,
                level_after=new_level if known else None, calculated_at=reference_at))
            event('run.completed', key, player, occurred_at, day_index)
            if known:
                rows['fact_xp_transactions'].append(dict(event_id=uid('event:xp.granted:' + key),
                    player_id=player, occurred_at=occurred_at, run_id=run_id, ruleset_id=ruleset,
                    source_type='DEDICATED_RUN', xp_delta=gained, level_after=new_level,
                    hero_kill_xp=components[0], tower_kill_xp=components[1],
                    tycoon_upgrade_xp=components[2], victory_xp=components[3]))
                event('xp.granted', key, player, occurred_at, day_index)
            if new_level > old_level:
                rows['fact_player_level_ups'].append(dict(event_id=uid('event:player.level_up:' + key),
                    player_id=player, occurred_at=occurred_at, run_id=run_id,
                    old_level=old_level, new_level=new_level, total_xp=xp_by_player[player]))
                event('player.level_up', key, player, occurred_at, day_index)
                for level in (2, 3, 5, 10, 15):
                    if not old_level < level <= new_level:
                        continue
                    gift_key = f'gift:{player}:{level}'
                    claim_time = occurred_at + timedelta(hours=rng.randrange(1, 72))
                    claimed = rng.random() < .78 and claim_time < reference_at
                    rows['fact_level_gift_claims'].append(dict(claim_id=uid(gift_key), player_id=player,
                        claimed_at=claim_time if claimed else None, rewarded_level=level,
                        reward_bundle_id=uid(f'level-bundle:{level}'), claim_status='CLAIMED' if claimed else 'PENDING'))
                    if claimed:
                        event('level_gift.claimed', gift_key, player, claim_time, day_index)

        case_count = max(1, int((22 + day_index*.35 + (25 if day_index >= 76 else 0)) * weekend * fraction))
        for index, completed in enumerate(sorted(at() for _ in range(case_count))):
            key = f'{day.date()}:case:{index}'
            player = rng.choice(players)
            case = rng.choices(('recruit', 'guardian', 'legend'), weights=(65, 27, 8))[0]
            counter = pity[player, case]
            guaranteed = counter >= 9
            rarity = rng.choices(('EPIC', 'LEGENDARY'), (90, 10))[0] if guaranteed else rng.choices(
                ('COMMON', 'RARE', 'EPIC', 'LEGENDARY'), (65, 25, 8, 2))[0]
            after = 0 if rarity in ('EPIC', 'LEGENDARY') else counter+1
            pity[player, case] = after
            rows['fact_case_settlements'].append(dict(opening_id=uid(key), player_id=player, completed_at=completed,
                case_id=uid('case:' + case), case_code=case, ruleset_id=release,
                reward_bundle_id=uid('case-bundle:' + rarity), payment_mode='INVENTORY' if rng.random()<.62 else 'WALLET',
                rarity=rarity, pity_before=counter, pity_after=after,
                guaranteed_triggered=int(guaranteed), entry_index=rng.randrange(6)))
            event('case.opened', key, player, completed, day_index)

        for index in range(max(1, int((12 + day_index*.25 + (18 if day_index >= 76 else 0)) * weekend * fraction))):
            key = f'{day.date()}:purchase:{index}'
            offer = rng.choices(('starter_sword', 'guardian_blade', 'archer_pack', 'weekly_gift'),
                               (42, 24, 16, 30 if day_index >= 76 else 8))[0]
            amount = dict(starter_sword=35, guardian_blade=120, archer_pack=75, weekly_gift=0)[offer]
            player = rng.choice(players[:280])
            completed = at()
            rows['fact_shop_purchases'].append(dict(purchase_id=uid(key), operation_id=uid('op:' + key),
                player_id=player, completed_at=completed, offer_id=offer, catalog_version=release,
                currency_code='PRESTIGE', charged_amount=amount,
                weapon_configuration_id='demo.' + offer, weapon_instance_id=uid('weapon:' + key)))
            event('shop.purchase_confirmed', key, player, completed, day_index)
    return rows


def expected_metrics(rows, reference_at):
    reference_at = utc_reference(reference_at)
    start = reference_at.replace(hour=0, minute=0, second=0, microsecond=0)-timedelta(days=29)
    end = start+timedelta(days=30)
    def window(table, timestamp):
        return [r for r in rows[table] if r.get(timestamp) is not None and start <= r[timestamp] < end]
    runs=window(TABLES[0], 'occurred_at'); xp=window(TABLES[1], 'occurred_at')
    levels=window(TABLES[2], 'occurred_at'); gifts=[r for r in window(TABLES[3], 'claimed_at') if r['claim_status']=='CLAIMED']
    cases=window(TABLES[4], 'completed_at'); shop=window(TABLES[5], 'completed_at'); events=window(TABLES[6], 'received_at')
    unique=lambda group: len({r['player_id'] for r in group})
    paid=[r for r in shop if r['charged_amount']>0]
    lags=[(r['received_at']-r['occurred_at']).total_seconds() for r in events]
    return {
        'runs': dict(runs=len(runs), players=unique(runs), victories=sum(r['outcome']=='victory' for r in runs),
            win_rate=sum(r['outcome']=='victory' for r in runs)/len(runs), known_xp=sum(r['xp_granted'] or 0 for r in runs),
            xp_coverage=sum(r['xp_granted'] is not None for r in runs)/len(runs),
            missing_xp_runs=sum(r['xp_granted'] is None for r in runs),
            avg_hero=sum(r['hero_kills'] for r in runs)/len(runs), avg_tower=sum(r['tower_kills'] for r in runs)/len(runs),
            avg_upgrades=sum(r['building_upgrades'] for r in runs)/len(runs)),
        'xp': dict(xp=sum(r['xp_delta'] for r in xp), transactions=len(xp),
            **{dest:sum(r[src] for r in xp) for dest,src in zip(('hero_xp','tower_xp','tycoon_xp','victory_xp'),
                ('hero_kill_xp','tower_kill_xp','tycoon_upgrade_xp','victory_xp'))}),
        'level_ups': dict(transitions=len(levels), levels=sum(r['new_level']-r['old_level'] for r in levels), players=unique(levels)),
        'level_gifts': dict(claims=len(gifts), players=unique(gifts)),
        'case_openings': dict(openings=len(cases), players=unique(cases), guarantees=sum(r['guaranteed_triggered'] for r in cases),
            guarantee_rate=sum(r['guaranteed_triggered'] for r in cases)/len(cases),
            per_player=len(cases)/unique(cases), avg_pity=sum(r['pity_before'] for r in cases)/len(cases)),
        'shop_purchases': dict(purchases=len(shop), players=unique(shop), prestige=sum(r['charged_amount'] for r in shop),
            paid_players=unique(paid), paid_purchases=len(paid), free_purchases=len(shop)-len(paid),
            paid_average=sum(r['charged_amount'] for r in paid)/len(paid), free_rate=(len(shop)-len(paid))/len(shop)),
        'event_delivery': dict(events=len(events), event_types=len({r['event_name'] for r in events}),
            average_lag=sum(v for v in lags if v>=0)/sum(v>=0 for v in lags),
            late_events=sum(v>300 for v in lags), clock_skew=sum(v<0 for v in lags)),
    }
