import csv
import importlib.util
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('active_budgets', Path(__file__).with_name('check-active-performance-budgets.py'))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class ActiveBudgetTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.directories = [self.root / 'first', self.root / 'second', self.root / 'holdout']
        self.sources = ['a' * 40, 'b' * 40]
        for directory, source in zip(self.directories, [*self.sources, 'c' * 40]):
            directory.mkdir()
            for suite, (header, fields, metric, count_field, count, expected) in module.SPECS.items():
                with (directory / (suite + '.csv')).open('w', newline='') as stream:
                    stream.write('.NET 10.0.13; Ubuntu 24.04.5 LTS; X64\n')
                    stream.write('cpu_count=4; server_gc=False; stopwatch_hz=1000000000; dynamic_code=True\n')
                    stream.write(f'library_package=0.1.0-dev; library_source={source}\n')
                    stream.write(f'harness_source={source}\n')
                    writer = csv.DictWriter(stream, [*fields, 'sample', count_field, metric])
                    writer.writeheader()
                    for key in expected:
                        for sample in range(5):
                            writer.writerow(dict(zip(fields, key), sample=sample, **{count_field: count, metric: '1000'}))
        self.budgets = module.calibrate(self.directories[:2], self.sources, 'd' * 40)

    def mutate(self, transform):
        path = self.directories[2] / 'channels.csv'
        lines = path.read_text().splitlines()
        rows = list(csv.DictReader(lines[4:]))
        rows = transform(rows)
        with path.open('w', newline='') as stream:
            stream.write('\n'.join(lines[:4]) + '\n')
            writer = csv.DictWriter(stream, rows[0].keys())
            writer.writeheader()
            writer.writerows(rows)

    def test_independent_holdout_accepts_fixed_budgets(self):
        result = module.check(self.directories[2], self.budgets)
        self.assertEqual(result['workloads'], 40)
        self.assertFalse(result['limits_auto_updated'])

    def test_active_allocation_regression_is_rejected(self):
        def change(rows):
            for row in rows:
                row['process_managed_bytes_per_message'] = '100000'
            return rows
        self.mutate(change)
        with self.assertRaisesRegex(module.core.BudgetViolation, 'Active allocation budget exceeded'):
            module.check(self.directories[2], self.budgets)

    def test_duplicate_and_missing_samples_are_rejected(self):
        self.mutate(lambda rows: rows[1:] + [rows[1]])
        with self.assertRaisesRegex(module.core.BudgetViolation, 'coverage mismatch'):
            module.check(self.directories[2], self.budgets)

    def test_two_training_processes_are_required(self):
        with self.assertRaisesRegex(module.core.BudgetViolation, 'Two independent'):
            module.calibrate(self.directories[:1], self.sources, 'd' * 40)

    def aot_repetitions(self):
        for i, directory in enumerate(self.directories, 1):
            for path in directory.glob('*.csv'):
                text = path.read_text().replace('dynamic_code=True', 'dynamic_code=False')
                for source in [*self.sources, 'c' * 40]: text = text.replace(source, 'a' * 40)
                path.write_text(text)
            directory.rename(self.root / f'active-{i}')

    def test_aot_repetitions_use_independent_holdout_and_new_cohort(self):
        self.aot_repetitions()
        limits = module.calibrate_aot_repetitions([self.root], 'a' * 40, 'd' * 40)
        self.assertEqual(module.check(self.root / 'active-3', limits)['workloads'], 40)
        self.assertTrue(all(key.endswith('dynamic_code=False') for key in limits['cohorts']))
        with self.assertRaisesRegex(ValueError, 'Unreviewed'):
            module.check(self.root / 'active-3', self.budgets)

    def test_aot_holdout_cannot_raise_the_ceiling(self):
        self.aot_repetitions()
        path = self.root / 'active-3' / 'channels.csv'
        path.write_text(path.read_text().replace(',1000', ',100000'))
        with self.assertRaisesRegex(ValueError, 'holdout'):
            module.calibrate_aot_repetitions([self.root], 'a' * 40, 'd' * 40)

    def test_unreviewed_hardware_is_rejected(self):
        path = self.directories[2] / 'channels.csv'
        path.write_text(path.read_text().replace('cpu_count=4', 'cpu_count=8'))
        with self.assertRaisesRegex(module.core.BudgetViolation, 'Mixed active'):
            module.check(self.directories[2], self.budgets)
