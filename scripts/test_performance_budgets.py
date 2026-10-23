import csv
import importlib.util
import itertools
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('performance_budgets', Path(__file__).with_name('check-performance-budgets.py'))
budget = importlib.util.module_from_spec(spec)
spec.loader.exec_module(budget)
SOURCE = 'a' * 40


class PerformanceBudgetsTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        self.crypto = [dict(profile=p, payload_bytes=str(length), sample=str(sample), roundtrips='5000',
                            managed_bytes_per_roundtrip='0.00', ns_per_roundtrip='100', managed_total_bytes='0')
                       for p, length, sample in itertools.product(budget.PROFILES, (60, 1200), range(7))]
        self.packets = [dict(operation=operation, sample=str(sample), operations='20000',
                             managed_bytes_per_op='0.00', ns_per_op='1', managed_total_bytes='0')
                        for operation, sample in itertools.product(budget.OPERATIONS, range(7))]
        self.idle = [dict(profile='harness_only' if pairs == 0 else 'AeadAes256Gcm',
                          channel_state=mode, trace=trace, pairs=str(pairs), sample=str(sample), elapsed_ms='2000',
                          process_bytes_per_second='1000', sctp_outstanding='0', sctp_buffered_send='0',
                          sctp_buffered_receive='0', sctp_retransmissions='0', retransmissions='0')
                     for (mode, trace, pairs), sample in itertools.product(
                         (('off', 'False', 0), *budget.IDLE_GROUPS), range(3))]
        self.write_all()

    def write(self, name, rows, source=SOURCE, runtime='10.0.13'):
        with (self.directory / name).open('w', newline='') as stream:
            stream.write(f'.NET {runtime}; Ubuntu 24.04.5 LTS; X64\n')
            stream.write('cpu_count=4; server_gc=False; stopwatch_hz=1000000000; dynamic_code=True\n')
            stream.write(f'library_package=0.2.10-dev.23; library_source={source}\n')
            stream.write(f'harness_source={source}\n')
            writer = csv.DictWriter(stream, rows[0].keys())
            writer.writeheader()
            writer.writerows(rows)

    def write_all(self, source=SOURCE, runtime='10.0.13'):
        self.write('performance.csv', self.crypto, source, runtime)
        self.write('packets.csv', self.packets, source, runtime)
        for repetition in (1, 2, 3):
            self.write(f'idle-sctp-{repetition}.csv', self.idle, source, runtime)

    def test_zero_and_calibrated_holdout_pass(self):
        limits = budget.calibrate([self.directory], SOURCE)
        result = budget.check(self.directory, limits)
        self.assertEqual(result['exact_zero_samples'], 42 + len(budget.ZERO_OPERATIONS) * 7)
        self.assertEqual(result['idle_processes'], 3)
        self.assertFalse(result['limits_auto_updated'])

    def test_unrounded_single_allocation_fails_exact_zero(self):
        self.crypto[0]['managed_total_bytes'] = '32'
        self.write('performance.csv', self.crypto)
        with self.assertRaisesRegex(budget.BudgetViolation, 'Exact zero-allocation'):
            budget.check(self.directory, {}, True)

    def test_missing_exact_counter_cannot_hide_behind_rounded_zero(self):
        for row in self.crypto:
            del row['managed_total_bytes']
        self.write('performance.csv', self.crypto)
        with self.assertRaisesRegex(budget.BudgetViolation, 'exact total'):
            budget.check(self.directory, {}, True)

    def test_measured_authentication_rows_are_required_but_not_claimed_zero(self):
        for row in self.packets:
            if row['operation'] not in budget.ZERO_OPERATIONS:
                row['managed_total_bytes'] = '123456'
        self.write('packets.csv', self.packets)
        budget.check(self.directory, {}, True)
        self.write('packets.csv', self.packets[:-1])
        with self.assertRaisesRegex(budget.BudgetViolation, 'Missing zero-contract samples'):
            budget.check(self.directory, {}, True)

    def test_writer_single_byte_regression_fails(self):
        for row in self.packets:
            if row['operation'] == 'stun-write-sha256':
                row['managed_total_bytes'] = '1'
                break
        self.write('packets.csv', self.packets)
        with self.assertRaisesRegex(budget.BudgetViolation, 'Exact zero-allocation'):
            budget.check(self.directory, {}, True)

    def test_deliberate_regression_is_rejected_after_training(self):
        limits = budget.calibrate([self.directory], SOURCE)
        for row in self.idle:
            if row['channel_state'] == 'open' and row['pairs'] == '1' and row['trace'] == 'False':
                row['process_bytes_per_second'] = '1000000'
        self.write('idle-sctp-3.csv', self.idle)
        with self.assertRaisesRegex(budget.BudgetViolation, 'Idle allocation budget exceeded'):
            budget.check(self.directory, limits)

    def test_independent_holdout_cannot_silently_raise_limits(self):
        for row in self.idle:
            row['process_bytes_per_second'] = '1000000'
        self.write('idle-sctp-3.csv', self.idle)
        with self.assertRaisesRegex(budget.BudgetViolation, 'holdout exceeds'):
            budget.calibrate([self.directory], SOURCE)

    def test_later_source_can_be_checked_against_reviewed_limits(self):
        limits = budget.calibrate([self.directory], SOURCE)
        self.write_all('b' * 40)
        self.assertEqual(budget.check(self.directory, limits)['source'], 'b' * 40)

    def test_runtime_drift_requires_review(self):
        limits = budget.calibrate([self.directory], SOURCE)
        self.write_all(runtime='10.0.14')
        with self.assertRaisesRegex(budget.BudgetViolation, 'Unreviewed runtime'):
            budget.check(self.directory, limits)

    def test_mixed_sources_fail(self):
        self.write('idle-sctp-2.csv', self.idle, 'b' * 40)
        with self.assertRaisesRegex(budget.BudgetViolation, 'Mixed source'):
            budget.calibrate([self.directory], SOURCE)

    def test_missing_or_duplicate_samples_fail(self):
        self.write('performance.csv', self.crypto[1:] + [self.crypto[1]])
        with self.assertRaisesRegex(budget.BudgetViolation, 'coverage mismatch'):
            budget.check(self.directory, {}, True)

    def test_nonfinite_or_undrained_idle_is_invalid(self):
        self.idle[0]['process_bytes_per_second'] = 'NaN'
        self.write('idle-sctp-1.csv', self.idle)
        with self.assertRaisesRegex(budget.BudgetViolation, 'Nonfinite'):
            budget.calibrate([self.directory], SOURCE)
        self.idle[0]['process_bytes_per_second'] = '1000'
        self.idle[0]['sctp_outstanding'] = '1'
        self.write('idle-sctp-1.csv', self.idle)
        with self.assertRaisesRegex(budget.BudgetViolation, 'not drained'):
            budget.calibrate([self.directory], SOURCE)

    def test_extreme_calibration_does_not_overflow_or_expand_limits(self):
        for row in self.idle:
            row['process_bytes_per_second'] = '1.7e308'
        for repetition in (1, 2, 3):
            self.write(f'idle-sctp-{repetition}.csv', self.idle)
        with self.assertRaisesRegex(budget.BudgetViolation, 'Unrepresentable'):
            budget.calibrate([self.directory], SOURCE)

    def test_wrong_calibration_source_fails(self):
        with self.assertRaisesRegex(budget.BudgetViolation, 'Calibration source mismatch'):
            budget.calibrate([self.directory], 'b' * 40)


if __name__ == '__main__':
    unittest.main()
