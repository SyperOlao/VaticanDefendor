"""Measurement correctness: decimal parsing, pooled tails, unavailable counters and failures."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

from motion_smoke import cpu_seconds, runtime_errors
from summarize_load import summarize


class LoadMetricsTests(unittest.TestCase):
    def test_cumulative_cpu_time(self):
        self.assertAlmostEqual(cpu_seconds('12:34.56'), 754.56)
        self.assertAlmostEqual(cpu_seconds('1:02:03.25'), 3723.25)

    def test_warning_stack_is_not_runtime_exception(self):
        self.assertEqual(runtime_errors("Unity.Entities.World:GetOrCreateSystemsAndLogException (types)"), [])
        self.assertEqual(len(runtime_errors("InvalidOperationException: failed\nSystem.ArgumentException: bad\nException: bad")), 3)

    def test_legacy_decimal_fields(self):
        path = Path(__file__).parents[2] / 'game_backend/scripts/summarize-stage11.py'
        spec = importlib.util.spec_from_file_location('legacy_summary', path)
        module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
        row = module.fields('[ServerTick] samples=300 avgMs=0.408 maxMs=1.234.')
        self.assertEqual(float(row['avgMs']), .408)
        self.assertEqual(float(row['maxMs']), 1.234)

    def test_pool_raw_samples_and_keep_failures(self):
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            result = dict(label='fixture', basicPassed=False, osSamples=[], serverErrors=['failed'], clientErrors=[])
            (folder / 'result.json').write_text(json.dumps(result))
            frame = ('[Stage11Metrics] frames=2 avgMs=10.00 gcAllocBytes=10 gcCollections=0/0/0 '
                     'netTxBytes=100 netRxBytes=200 snapshotRecorder=False rttMs=200\n'
                     '[Stage11FrameSamples] durationUs=1000,19000\n')
            tick = ('[ServerTick] samples=2 avgMs=0.125 maxMs=0.200 livingEnemies=512 paused=0 terminal=0\n'
                    '[ServerTickSamples] durationUs=50,200\n')
            (folder / 'client.log').write_text(frame * 3 + ('[CorrectionProbe] seconds=5.000 heroCorrections=30 enemyCorrections=60 maxAdjustmentMeters=0.1 overflow=0\n' * 3))
            (folder / 'server.log').write_text((frame + tick) * 3)
            row = summarize(folder)
            self.assertEqual(row['tickRawSamples'], 2)
            self.assertAlmostEqual(row['tickP99Ms'], .2)
            self.assertAlmostEqual(row['clientFrameP95Ms'], 19)
            self.assertIsNone(row['serverSnapshots'])
            self.assertFalse(row['serverSnapshotBytesAvailable'])
            self.assertEqual(row['heroSmoothingCorrectionsPerSecond'], 6)
            self.assertEqual(row['enemySmoothingCorrectionsPerSecond'], 12)
            self.assertFalse(row['basicPassed'])
            self.assertEqual(row['serverErrors'], ['failed'])


if __name__ == '__main__':
    unittest.main()
