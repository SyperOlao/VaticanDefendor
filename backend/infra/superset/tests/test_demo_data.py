"""Recovery and causal links must survive changes to presentation and seed tooling."""
from collections import defaultdict
from datetime import datetime, timezone
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from demo_data import generate, expected_metrics


class DemoDataTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.reference = datetime(2026, 11, 17, 11, tzinfo=timezone.utc)
        cls.rows = generate(cls.reference)

    def test_recovery_from_naive_clickhouse_timestamp_is_identical(self):
        recovered = generate(self.reference.replace(tzinfo=None))
        self.assertEqual(self.rows, recovered)
        self.assertEqual(expected_metrics(self.rows, self.reference),
                         expected_metrics(recovered, self.reference.replace(tzinfo=None)))

    def test_xp_transactions_link_to_known_runs_and_raw_events(self):
        runs = {r['run_id']: r for r in self.rows['fact_run_settlements']}
        events = {r['event_id']: r for r in self.rows['raw_game_events']}
        for row in self.rows['fact_xp_transactions']:
            self.assertEqual(row['xp_delta'], sum(row[key] for key in
                ('hero_kill_xp','tower_kill_xp','tycoon_upgrade_xp','victory_xp')))
            self.assertEqual(row['xp_delta'], runs[row['run_id']]['xp_granted'])
            self.assertEqual(events[row['event_id']]['event_name'], 'xp.granted')
        totals = expected_metrics(self.rows, self.reference)
        self.assertEqual(totals['runs']['known_xp'], totals['xp']['xp'])
        self.assertGreater(totals['runs']['missing_xp_runs'], 0)
        self.assertGreater(totals['event_delivery']['clock_skew'], 0)
        self.assertGreater(totals['shop_purchases']['free_purchases'], 0)

    def test_pity_is_continuous_in_actual_time_order(self):
        previous = defaultdict(int)
        for row in sorted(self.rows['fact_case_settlements'], key=lambda r:r['completed_at']):
            key = row['player_id'], row['case_code']
            self.assertEqual(previous[key], row['pity_before'])
            self.assertEqual(row['guaranteed_triggered'], int(row['pity_before'] >= 9))
            expected = 0 if row['rarity'] in ('EPIC','LEGENDARY') else row['pity_before']+1
            self.assertEqual(row['pity_after'], expected)
            previous[key] = row['pity_after']


if __name__ == '__main__':
    unittest.main()
