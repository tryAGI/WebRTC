import copy
import importlib.util
from pathlib import Path
import unittest
spec = importlib.util.spec_from_file_location('load', Path(__file__).with_name('check-authentication-load.py'))
load = importlib.util.module_from_spec(spec); spec.loader.exec_module(load)


def fixture(control=False):
    return [dict(profile=profile, burst=str(burst), sample=str(sample), offered='100', received='100', missing='0',
                 injected=str(burst * 100), rejected=str(burst * 100), ice_drops='0', media_drops='0', fixture_drops='0',
                 process_bytes='0', cpu_ms='1', elapsed_ms='2000', p50_ns='1', p95_ns='2', p99_ns='3', gen0='0', gen1='0', gen2='0')
            for profile in (('AeadAes128Gcm',) if control else load.core.PROFILES)
            for burst in ((0,) if control else (0, 16, 64, 256)) for sample in range(3)]


class AuthenticationLoadTests(unittest.TestCase):
    def test_coverage(self):
        rows = fixture(); self.assertEqual(load.validate(rows)['rows'], 36)
        self.assertEqual(load.validate(rows)['packet_accounting_residual']['total'], 0)
        for bad in (rows[:-1], rows + [rows[0]]):
            with self.assertRaises(ValueError): load.validate(bad)

    def test_bad_counters_and_latency_fail(self):
        for key, value in [('missing', '1'), ('injected', '1'), ('rejected', '1'), ('process_bytes', '-1'),
                           ('cpu_ms', 'NaN'), ('p95_ns', '0'), ('gen0', '.5'), ('fixture_drops', '1')]:
            rows = fixture(); rows[0][key] = value
            with self.assertRaises(ValueError): load.validate(rows)

    def test_real_loss_control_is_distinct_from_incomplete_coverage(self):
        rows = fixture(True); rows[0].update(received='99', missing='1', fixture_drops='1')
        self.assertTrue(load.validate(rows, True)['real_packet_drop_detected'])
        rows[0]['fixture_drops'] = '0'
        with self.assertRaises(ValueError): load.validate(rows, True)
        rows = fixture(); rows[0].update(received='99', missing='1')
        with self.assertRaises(load.DeliveryViolation): load.validate(rows)

    def test_treatment_loss_retains_censoring_and_does_not_claim_all_rejected(self):
        rows = fixture(); rows[3].update(received='90', missing='10', rejected='1500', ice_drops='10')
        self.assertEqual(load.validate(rows)['treatment_losses'], 10)
        self.assertEqual(load.validate(rows)['packet_accounting_residual']['max'], 100)


if __name__ == '__main__': unittest.main()
