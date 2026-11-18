-- Read-only diagnostics. Compare source and snapshot after a successful refresh;
-- concurrent ingestion can change the source between these queries.
SET prefer_column_name_to_alias = 1;

-- runs: exact metrics for the same UTC period
SELECT count() AS `runs`, uniqExact(player_id) AS `players`, countIf(outcome = 'victory') AS `victories`, countIf(outcome = 'victory') / nullIf(count(), 0) AS `win_rate`, sum(known_xp) AS `known_xp`, countIf(xp_known = 1) / nullIf(count(), 0) AS `xp_coverage`, countIf(xp_known = 0) AS `missing_xp_runs`, sum(toDecimal128(hero_kills, 0)) AS `hero_kills`, sum(toDecimal128(tower_kills, 0)) AS `tower_kills`, sum(toDecimal128(building_upgrades, 0)) AS `upgrades`, avg(hero_kills) AS `avg_hero`, avg(tower_kills) AS `avg_tower`, avg(building_upgrades) AS `avg_upgrades` FROM `vatican_bi`.`runs` WHERE `occurred_at` >= toStartOfDay(now('UTC')) - INTERVAL 29 DAY AND `occurred_at` < toStartOfDay(now('UTC')) + INTERVAL 1 DAY;

EXPLAIN indexes = 1 SELECT count() AS `runs`, uniqExact(player_id) AS `players`, countIf(outcome = 'victory') AS `victories`, countIf(outcome = 'victory') / nullIf(count(), 0) AS `win_rate`, sum(known_xp) AS `known_xp`, countIf(xp_known = 1) / nullIf(count(), 0) AS `xp_coverage`, countIf(xp_known = 0) AS `missing_xp_runs`, sum(toDecimal128(hero_kills, 0)) AS `hero_kills`, sum(toDecimal128(tower_kills, 0)) AS `tower_kills`, sum(toDecimal128(building_upgrades, 0)) AS `upgrades`, avg(hero_kills) AS `avg_hero`, avg(tower_kills) AS `avg_tower`, avg(building_upgrades) AS `avg_upgrades` FROM `vatican_bi`.`runs` WHERE `occurred_at` >= toStartOfDay(now('UTC')) - INTERVAL 29 DAY AND `occurred_at` < toStartOfDay(now('UTC')) + INTERVAL 1 DAY;

-- runs_snapshot: exact metrics for the same UTC period
SELECT count() AS `runs`, uniqExact(player_id) AS `players`, countIf(outcome = 'victory') AS `victories`, countIf(outcome = 'victory') / nullIf(count(), 0) AS `win_rate`, sum(known_xp) AS `known_xp`, countIf(xp_known = 1) / nullIf(count(), 0) AS `xp_coverage`, countIf(xp_known = 0) AS `missing_xp_runs`, sum(toDecimal128(hero_kills, 0)) AS `hero_kills`, sum(toDecimal128(tower_kills, 0)) AS `tower_kills`, sum(toDecimal128(building_upgrades, 0)) AS `upgrades`, avg(hero_kills) AS `avg_hero`, avg(tower_kills) AS `avg_tower`, avg(building_upgrades) AS `avg_upgrades` FROM `vatican_bi`.`runs_snapshot` WHERE `occurred_at` >= toStartOfDay(now('UTC')) - INTERVAL 29 DAY AND `occurred_at` < toStartOfDay(now('UTC')) + INTERVAL 1 DAY;

EXPLAIN indexes = 1 SELECT count() AS `runs`, uniqExact(player_id) AS `players`, countIf(outcome = 'victory') AS `victories`, countIf(outcome = 'victory') / nullIf(count(), 0) AS `win_rate`, sum(known_xp) AS `known_xp`, countIf(xp_known = 1) / nullIf(count(), 0) AS `xp_coverage`, countIf(xp_known = 0) AS `missing_xp_runs`, sum(toDecimal128(hero_kills, 0)) AS `hero_kills`, sum(toDecimal128(tower_kills, 0)) AS `tower_kills`, sum(toDecimal128(building_upgrades, 0)) AS `upgrades`, avg(hero_kills) AS `avg_hero`, avg(tower_kills) AS `avg_tower`, avg(building_upgrades) AS `avg_upgrades` FROM `vatican_bi`.`runs_snapshot` WHERE `occurred_at` >= toStartOfDay(now('UTC')) - INTERVAL 29 DAY AND `occurred_at` < toStartOfDay(now('UTC')) + INTERVAL 1 DAY;

-- xp: exact metrics for the same UTC period
SELECT sum(xp_delta) AS `xp`, if(count(hero_xp) = 0, NULL, sum(hero_xp)) AS `hero_xp`, if(count(tower_xp) = 0, NULL, sum(tower_xp)) AS `tower_xp`, if(count(tycoon_xp) = 0, NULL, sum(tycoon_xp)) AS `tycoon_xp`, if(count(victory_xp) = 0, NULL, sum(victory_xp)) AS `victory_xp`, count() AS `transactions` FROM `vatican_bi`.`xp` WHERE `occurred_at` >= toStartOfDay(now('UTC')) - INTERVAL 29 DAY AND `occurred_at` < toStartOfDay(now('UTC')) + INTERVAL 1 DAY;

EXPLAIN indexes = 1 SELECT sum(xp_delta) AS `xp`, if(count(hero_xp) = 0, NULL, sum(hero_xp)) AS `hero_xp`, if(count(tower_xp) = 0, NULL, sum(tower_xp)) AS `tower_xp`, if(count(tycoon_xp) = 0, NULL, sum(tycoon_xp)) AS `tycoon_xp`, if(count(victory_xp) = 0, NULL, sum(victory_xp)) AS `victory_xp`, count() AS `transactions` FROM `vatican_bi`.`xp` WHERE `occurred_at` >= toStartOfDay(now('UTC')) - INTERVAL 29 DAY AND `occurred_at` < toStartOfDay(now('UTC')) + INTERVAL 1 DAY;

-- xp_snapshot: exact metrics for the same UTC period
SELECT sum(xp_delta) AS `xp`, if(count(hero_xp) = 0, NULL, sum(hero_xp)) AS `hero_xp`, if(count(tower_xp) = 0, NULL, sum(tower_xp)) AS `tower_xp`, if(count(tycoon_xp) = 0, NULL, sum(tycoon_xp)) AS `tycoon_xp`, if(count(victory_xp) = 0, NULL, sum(victory_xp)) AS `victory_xp`, count() AS `transactions` FROM `vatican_bi`.`xp_snapshot` WHERE `occurred_at` >= toStartOfDay(now('UTC')) - INTERVAL 29 DAY AND `occurred_at` < toStartOfDay(now('UTC')) + INTERVAL 1 DAY;

EXPLAIN indexes = 1 SELECT sum(xp_delta) AS `xp`, if(count(hero_xp) = 0, NULL, sum(hero_xp)) AS `hero_xp`, if(count(tower_xp) = 0, NULL, sum(tower_xp)) AS `tower_xp`, if(count(tycoon_xp) = 0, NULL, sum(tycoon_xp)) AS `tycoon_xp`, if(count(victory_xp) = 0, NULL, sum(victory_xp)) AS `victory_xp`, count() AS `transactions` FROM `vatican_bi`.`xp_snapshot` WHERE `occurred_at` >= toStartOfDay(now('UTC')) - INTERVAL 29 DAY AND `occurred_at` < toStartOfDay(now('UTC')) + INTERVAL 1 DAY;

-- level_ups: exact metrics for the same UTC period
SELECT count() AS `transitions`, sum(levels_gained) AS `levels`, uniqExact(player_id) AS `players` FROM `vatican_bi`.`level_ups` WHERE `occurred_at` >= toStartOfDay(now('UTC')) - INTERVAL 29 DAY AND `occurred_at` < toStartOfDay(now('UTC')) + INTERVAL 1 DAY;

EXPLAIN indexes = 1 SELECT count() AS `transitions`, sum(levels_gained) AS `levels`, uniqExact(player_id) AS `players` FROM `vatican_bi`.`level_ups` WHERE `occurred_at` >= toStartOfDay(now('UTC')) - INTERVAL 29 DAY AND `occurred_at` < toStartOfDay(now('UTC')) + INTERVAL 1 DAY;

-- level_ups_snapshot: exact metrics for the same UTC period
SELECT count() AS `transitions`, sum(levels_gained) AS `levels`, uniqExact(player_id) AS `players` FROM `vatican_bi`.`level_ups_snapshot` WHERE `occurred_at` >= toStartOfDay(now('UTC')) - INTERVAL 29 DAY AND `occurred_at` < toStartOfDay(now('UTC')) + INTERVAL 1 DAY;

EXPLAIN indexes = 1 SELECT count() AS `transitions`, sum(levels_gained) AS `levels`, uniqExact(player_id) AS `players` FROM `vatican_bi`.`level_ups_snapshot` WHERE `occurred_at` >= toStartOfDay(now('UTC')) - INTERVAL 29 DAY AND `occurred_at` < toStartOfDay(now('UTC')) + INTERVAL 1 DAY;

-- level_gifts: exact metrics for the same UTC period
SELECT count() AS `claims`, uniqExact(player_id) AS `players` FROM `vatican_bi`.`level_gifts` WHERE `occurred_at` >= toStartOfDay(now('UTC')) - INTERVAL 29 DAY AND `occurred_at` < toStartOfDay(now('UTC')) + INTERVAL 1 DAY;

EXPLAIN indexes = 1 SELECT count() AS `claims`, uniqExact(player_id) AS `players` FROM `vatican_bi`.`level_gifts` WHERE `occurred_at` >= toStartOfDay(now('UTC')) - INTERVAL 29 DAY AND `occurred_at` < toStartOfDay(now('UTC')) + INTERVAL 1 DAY;

-- level_gifts_snapshot: exact metrics for the same UTC period
SELECT count() AS `claims`, uniqExact(player_id) AS `players` FROM `vatican_bi`.`level_gifts_snapshot` WHERE `occurred_at` >= toStartOfDay(now('UTC')) - INTERVAL 29 DAY AND `occurred_at` < toStartOfDay(now('UTC')) + INTERVAL 1 DAY;

EXPLAIN indexes = 1 SELECT count() AS `claims`, uniqExact(player_id) AS `players` FROM `vatican_bi`.`level_gifts_snapshot` WHERE `occurred_at` >= toStartOfDay(now('UTC')) - INTERVAL 29 DAY AND `occurred_at` < toStartOfDay(now('UTC')) + INTERVAL 1 DAY;

-- case_openings: exact metrics for the same UTC period
SELECT count() AS `openings`, uniqExact(player_id) AS `players`, countIf(guaranteed_triggered = 1) AS `guarantees`, countIf(guaranteed_triggered = 1) / nullIf(count(), 0) AS `guarantee_rate`, avg(pity_before) AS `avg_pity`, count() / nullIf(uniqExact(player_id), 0) AS `per_player` FROM `vatican_bi`.`case_openings` WHERE `occurred_at` >= toStartOfDay(now('UTC')) - INTERVAL 29 DAY AND `occurred_at` < toStartOfDay(now('UTC')) + INTERVAL 1 DAY;

EXPLAIN indexes = 1 SELECT count() AS `openings`, uniqExact(player_id) AS `players`, countIf(guaranteed_triggered = 1) AS `guarantees`, countIf(guaranteed_triggered = 1) / nullIf(count(), 0) AS `guarantee_rate`, avg(pity_before) AS `avg_pity`, count() / nullIf(uniqExact(player_id), 0) AS `per_player` FROM `vatican_bi`.`case_openings` WHERE `occurred_at` >= toStartOfDay(now('UTC')) - INTERVAL 29 DAY AND `occurred_at` < toStartOfDay(now('UTC')) + INTERVAL 1 DAY;

-- case_openings_snapshot: exact metrics for the same UTC period
SELECT count() AS `openings`, uniqExact(player_id) AS `players`, countIf(guaranteed_triggered = 1) AS `guarantees`, countIf(guaranteed_triggered = 1) / nullIf(count(), 0) AS `guarantee_rate`, avg(pity_before) AS `avg_pity`, count() / nullIf(uniqExact(player_id), 0) AS `per_player` FROM `vatican_bi`.`case_openings_snapshot` WHERE `occurred_at` >= toStartOfDay(now('UTC')) - INTERVAL 29 DAY AND `occurred_at` < toStartOfDay(now('UTC')) + INTERVAL 1 DAY;

EXPLAIN indexes = 1 SELECT count() AS `openings`, uniqExact(player_id) AS `players`, countIf(guaranteed_triggered = 1) AS `guarantees`, countIf(guaranteed_triggered = 1) / nullIf(count(), 0) AS `guarantee_rate`, avg(pity_before) AS `avg_pity`, count() / nullIf(uniqExact(player_id), 0) AS `per_player` FROM `vatican_bi`.`case_openings_snapshot` WHERE `occurred_at` >= toStartOfDay(now('UTC')) - INTERVAL 29 DAY AND `occurred_at` < toStartOfDay(now('UTC')) + INTERVAL 1 DAY;

-- shop_purchases: exact metrics for the same UTC period
SELECT count() AS `purchases`, uniqExact(player_id) AS `players`, sumIf(charged_amount, currency_code = 'PRESTIGE') AS `prestige`, avgIf(charged_amount, currency_code = 'PRESTIGE') AS `average_price`, avgIf(charged_amount, currency_code = 'PRESTIGE' AND charged_amount > 0) AS `paid_average`, uniqExactIf(player_id, currency_code = 'PRESTIGE' AND charged_amount > 0) AS `paid_players`, countIf(charged_amount > 0 AND currency_code = 'PRESTIGE') AS `paid_purchases`, countIf(charged_amount = 0) AS `free_purchases`, countIf(charged_amount = 0) / nullIf(count(), 0) AS `free_rate` FROM `vatican_bi`.`shop_purchases` WHERE `occurred_at` >= toStartOfDay(now('UTC')) - INTERVAL 29 DAY AND `occurred_at` < toStartOfDay(now('UTC')) + INTERVAL 1 DAY;

EXPLAIN indexes = 1 SELECT count() AS `purchases`, uniqExact(player_id) AS `players`, sumIf(charged_amount, currency_code = 'PRESTIGE') AS `prestige`, avgIf(charged_amount, currency_code = 'PRESTIGE') AS `average_price`, avgIf(charged_amount, currency_code = 'PRESTIGE' AND charged_amount > 0) AS `paid_average`, uniqExactIf(player_id, currency_code = 'PRESTIGE' AND charged_amount > 0) AS `paid_players`, countIf(charged_amount > 0 AND currency_code = 'PRESTIGE') AS `paid_purchases`, countIf(charged_amount = 0) AS `free_purchases`, countIf(charged_amount = 0) / nullIf(count(), 0) AS `free_rate` FROM `vatican_bi`.`shop_purchases` WHERE `occurred_at` >= toStartOfDay(now('UTC')) - INTERVAL 29 DAY AND `occurred_at` < toStartOfDay(now('UTC')) + INTERVAL 1 DAY;

-- shop_purchases_snapshot: exact metrics for the same UTC period
SELECT count() AS `purchases`, uniqExact(player_id) AS `players`, sumIf(charged_amount, currency_code = 'PRESTIGE') AS `prestige`, avgIf(charged_amount, currency_code = 'PRESTIGE') AS `average_price`, avgIf(charged_amount, currency_code = 'PRESTIGE' AND charged_amount > 0) AS `paid_average`, uniqExactIf(player_id, currency_code = 'PRESTIGE' AND charged_amount > 0) AS `paid_players`, countIf(charged_amount > 0 AND currency_code = 'PRESTIGE') AS `paid_purchases`, countIf(charged_amount = 0) AS `free_purchases`, countIf(charged_amount = 0) / nullIf(count(), 0) AS `free_rate` FROM `vatican_bi`.`shop_purchases_snapshot` WHERE `occurred_at` >= toStartOfDay(now('UTC')) - INTERVAL 29 DAY AND `occurred_at` < toStartOfDay(now('UTC')) + INTERVAL 1 DAY;

EXPLAIN indexes = 1 SELECT count() AS `purchases`, uniqExact(player_id) AS `players`, sumIf(charged_amount, currency_code = 'PRESTIGE') AS `prestige`, avgIf(charged_amount, currency_code = 'PRESTIGE') AS `average_price`, avgIf(charged_amount, currency_code = 'PRESTIGE' AND charged_amount > 0) AS `paid_average`, uniqExactIf(player_id, currency_code = 'PRESTIGE' AND charged_amount > 0) AS `paid_players`, countIf(charged_amount > 0 AND currency_code = 'PRESTIGE') AS `paid_purchases`, countIf(charged_amount = 0) AS `free_purchases`, countIf(charged_amount = 0) / nullIf(count(), 0) AS `free_rate` FROM `vatican_bi`.`shop_purchases_snapshot` WHERE `occurred_at` >= toStartOfDay(now('UTC')) - INTERVAL 29 DAY AND `occurred_at` < toStartOfDay(now('UTC')) + INTERVAL 1 DAY;

-- event_delivery: exact metrics for the same UTC period
SELECT count() AS `events`, uniqExact(event_name) AS `event_types`, avgIf(lag_seconds, lag_seconds >= 0) AS `average_lag`, countIf(lag_seconds > 300) AS `late_events`, countIf(lag_seconds < 0) AS `clock_skew` FROM `vatican_bi`.`event_delivery` WHERE `received_at` >= toStartOfDay(now('UTC')) - INTERVAL 29 DAY AND `received_at` < toStartOfDay(now('UTC')) + INTERVAL 1 DAY;

EXPLAIN indexes = 1 SELECT count() AS `events`, uniqExact(event_name) AS `event_types`, avgIf(lag_seconds, lag_seconds >= 0) AS `average_lag`, countIf(lag_seconds > 300) AS `late_events`, countIf(lag_seconds < 0) AS `clock_skew` FROM `vatican_bi`.`event_delivery` WHERE `received_at` >= toStartOfDay(now('UTC')) - INTERVAL 29 DAY AND `received_at` < toStartOfDay(now('UTC')) + INTERVAL 1 DAY;

-- event_delivery_snapshot: exact metrics for the same UTC period
SELECT count() AS `events`, uniqExact(event_name) AS `event_types`, avgIf(lag_seconds, lag_seconds >= 0) AS `average_lag`, countIf(lag_seconds > 300) AS `late_events`, countIf(lag_seconds < 0) AS `clock_skew` FROM `vatican_bi`.`event_delivery_snapshot` WHERE `received_at` >= toStartOfDay(now('UTC')) - INTERVAL 29 DAY AND `received_at` < toStartOfDay(now('UTC')) + INTERVAL 1 DAY;

EXPLAIN indexes = 1 SELECT count() AS `events`, uniqExact(event_name) AS `event_types`, avgIf(lag_seconds, lag_seconds >= 0) AS `average_lag`, countIf(lag_seconds > 300) AS `late_events`, countIf(lag_seconds < 0) AS `clock_skew` FROM `vatican_bi`.`event_delivery_snapshot` WHERE `received_at` >= toStartOfDay(now('UTC')) - INTERVAL 29 DAY AND `received_at` < toStartOfDay(now('UTC')) + INTERVAL 1 DAY;
