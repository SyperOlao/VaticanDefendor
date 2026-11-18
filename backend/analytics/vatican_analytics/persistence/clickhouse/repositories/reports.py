"""Calculate complete daily totals from FINAL facts; no incremental writes."""
from datetime import date, datetime, time, timedelta, timezone

from ....application.errors import ApplicationError


# Decimal128 sums prevent UInt64 wraparound when many valid counters/XP are added.
# The result contains no player IDs, raw payloads, or invented simulation metrics.
DAILY_SQL = """
SELECT * FROM
(
    SELECT count() AS runs_completed, uniqExact(player_id) AS runs_players,
        countIf(outcome = 'victory') AS runs_victories,
        countIf(outcome = 'defeat') AS runs_defeats,
        sum(toDecimal128(hero_kills, 0)) AS runs_hero_kills,
        sum(toDecimal128(tower_kills, 0)) AS runs_tower_kills,
        sum(toDecimal128(building_upgrades, 0)) AS runs_building_upgrades,
        sum(toDecimal128(ifNull(xp_granted, 0), 0)) AS runs_known_xp_awarded,
        countIf(isNotNull(xp_granted)) AS runs_with_xp,
        countIf(isNull(xp_granted)) AS runs_without_xp
    FROM fact_run_settlements FINAL
    WHERE occurred_at >= toDateTime64(%(start)s, 3, 'UTC')
        AND occurred_at < toDateTime64(%(end)s, 3, 'UTC')
) AS runs
CROSS JOIN
(
    SELECT count() AS levels_transitions, uniqExact(player_id) AS levels_players,
        sum(toDecimal128(new_level, 0) - toDecimal128(old_level, 0)) AS levels_gained
    FROM fact_player_level_ups FINAL
    WHERE occurred_at >= toDateTime64(%(start)s, 3, 'UTC')
        AND occurred_at < toDateTime64(%(end)s, 3, 'UTC')
) AS levels
CROSS JOIN
(
    SELECT count() AS gifts_claims, uniqExact(player_id) AS gifts_players
    FROM fact_level_gift_claims FINAL
    WHERE claimed_at >= toDateTime64(%(start)s, 3, 'UTC')
        AND claimed_at < toDateTime64(%(end)s, 3, 'UTC')
        AND claim_status = 'CLAIMED'
) AS gifts
CROSS JOIN
(
    SELECT count() AS cases_openings, uniqExact(player_id) AS cases_players,
        countIf(guaranteed_triggered = 1) AS cases_guaranteed,
        countIf(payment_mode = 'INVENTORY') AS cases_inventory,
        countIf(payment_mode = 'WALLET') AS cases_wallet
    FROM fact_case_settlements FINAL
    WHERE completed_at >= toDateTime64(%(start)s, 3, 'UTC')
        AND completed_at < toDateTime64(%(end)s, 3, 'UTC')
) AS cases
SETTINGS max_execution_time = 3, max_result_rows = 1, result_overflow_mode = 'throw'
"""


class ClickHouseDailyReports:
    def __init__(self, database):
        self.database = database

    def daily(self, day: date) -> dict:
        start = datetime.combine(day, time(), tzinfo=timezone.utc)
        parameters = {'start': start.strftime('%Y-%m-%d %H:%M:%S'),
                      'end': (start + timedelta(days=1)).strftime('%Y-%m-%d %H:%M:%S')}
        try:
            with self.database.engine.connect() as connection:
                values = connection.exec_driver_sql(DAILY_SQL, parameters).mappings().one()
                return {
                    'runs': {
                        'completed': int(values['runs_completed']),
                        'players': int(values['runs_players']),
                        'victories': int(values['runs_victories']),
                        'defeats': int(values['runs_defeats']),
                        'heroKills': int(values['runs_hero_kills']),
                        'towerKills': int(values['runs_tower_kills']),
                        'buildingUpgrades': int(values['runs_building_upgrades']),
                        'knownXpAwarded': int(values['runs_known_xp_awarded']),
                        'runsWithXp': int(values['runs_with_xp']),
                        'runsWithoutXp': int(values['runs_without_xp']),
                    },
                    'levelUps': {
                        'transitions': int(values['levels_transitions']),
                        'players': int(values['levels_players']),
                        'levelsGained': int(values['levels_gained']),
                    },
                    'levelGifts': {
                        'claims': int(values['gifts_claims']),
                        'players': int(values['gifts_players']),
                    },
                    'cases': {
                        'openings': int(values['cases_openings']),
                        'players': int(values['cases_players']),
                        'guaranteedOpenings': int(values['cases_guaranteed']),
                        'paidFromInventory': int(values['cases_inventory']),
                        'paidFromWallet': int(values['cases_wallet']),
                    },
                }
        except Exception:
            # Includes HTTP driver transport errors, absent schema, and query timeouts.
            # Never expose SQL, connection credentials, or partial totals to callers.
            raise ApplicationError(503, 'Analytics report is temporarily unavailable') from None
