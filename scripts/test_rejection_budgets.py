import csv
import importlib.util
import itertools
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('rejection_budget', Path(__file__).with_name('check-rejection-budgets.py'))
budget = importlib.util.module_from_spec(spec)
spec.loader.exec_module(budget)
SOURCE = 'a' * 40


class RejectionBudgets(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.rows = [dict(profile=p, packet=packet, packet_bytes=str(budget.PACKETS[packet]), mode=mode,
                          sample=str(sample), operations='1000', managed_total_bytes='0' if p == budget.base.PROFILES[0] or mode == 'replay' else '512000',
                          process_total_bytes='1000000', ns_per_op='10', cpu_ms='1', gen0='0', gen1='0', gen2='0')
                     for (p, packet, mode), sample in itertools.product(budget.GROUPS, range(7))]
        for i in (1, 2, 3): self.write(i)

    def write(self, i, rows=None, dynamic='True', source=SOURCE):
        rows = self.rows if rows is None else rows
        with (self.root / f'rejection-{i}.csv').open('w') as stream:
            stream.write(f'.NET 10.0.13; Ubuntu 24.04.5 LTS; X64\ncpu_count=4; server_gc=False; stopwatch_hz=1000000000; dynamic_code={dynamic}\nlibrary_package=0.2.10-dev.34; library_source={source}\nharness_source={source}\n')
            writer = csv.DictWriter(stream, rows[0].keys()); writer.writeheader(); writer.writerows(rows)

    def test_capture_calibration_and_fresh_check(self):
        self.assertEqual(budget.check(self.root)['verdict'], 'capture_valid')
        limits = budget.calibrate([self.root], SOURCE)
        self.assertEqual(budget.check(self.root, limits)['verdict'], 'pass')
        self.assertEqual(len(limits['cohorts']), 1)

    def test_replay_one_byte_cannot_be_rounded_away(self):
        next(r for r in self.rows if r['mode'] == 'replay')['managed_total_bytes'] = '1'
        self.write(1)
        with self.assertRaisesRegex(ValueError, 'Exact rejection'): budget.check(self.root)

    def test_one_bad_sample_exceeds_budget(self):
        limits = budget.calibrate([self.root], SOURCE)
        next(r for r in self.rows if r['managed_total_bytes'] != '0')['managed_total_bytes'] = '2000000'
        self.write(3)
        with self.assertRaisesRegex(ValueError, 'budget exceeded'): budget.check(self.root, limits)
        with self.assertRaisesRegex(ValueError, 'holdout'): budget.calibrate([self.root], SOURCE)

    def test_aot_is_separate_cohort(self):
        limits = budget.calibrate([self.root], SOURCE)
        for i in (1, 2, 3): self.write(i, dynamic='False')
        with self.assertRaisesRegex(ValueError, 'Unreviewed'): budget.check(self.root, limits)

    def test_missing_duplicate_and_nonfinite_fail(self):
        for rows in (self.rows[:-1], self.rows[:-1] + [self.rows[0]]):
            self.write(1, rows)
            with self.assertRaises(ValueError): budget.check(self.root)
        self.rows[0]['ns_per_op'] = 'NaN'; self.write(1)
        with self.assertRaisesRegex(ValueError, 'Nonfinite'): budget.check(self.root)

    def test_framework_failure_cost_can_move_between_scenarios(self):
        key = ('AeadAes256Gcm', 'rtp-video', 'bad-tag')
        for row in self.rows:
            if (row['profile'], row['packet'], row['mode']) == key: row['managed_total_bytes'] = '192000'
        for i in (1, 2, 3): self.write(i)
        limits = budget.calibrate([self.root], SOURCE)
        for row in self.rows:
            if (row['profile'], row['packet'], row['mode']) == key: row['managed_total_bytes'] = '600000'
        self.write(3)
        self.assertEqual(budget.check(self.root, limits)['verdict'], 'pass')

    def test_mixed_source_fails(self):
        self.write(3, source='b' * 40)
        with self.assertRaisesRegex(ValueError, 'Mixed'): budget.check(self.root)


if __name__ == '__main__': unittest.main()
