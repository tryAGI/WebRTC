import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('retention_profile', Path(__file__).with_name('profile-retention-macos.py'))
profile = importlib.util.module_from_spec(spec)
spec.loader.exec_module(profile)


class BarrierTests(unittest.TestCase):
    def test_ready_and_disposed_phase(self):
        self.assertEqual(profile.barrier('# profile_barrier=ready;wave=-5;alive_disposed_peers=0', -5), -5)
        self.assertEqual(profile.barrier('# profile_barrier=disposed;wave=2;alive_disposed_peers=0', 2), 2)
        self.assertIsNone(profile.barrier('peer_open,False,2,disposed', 2))

    def test_mismatched_wave_phase_and_live_count_fail(self):
        for line in ('# profile_barrier=disposed;wave=16;alive_disposed_peers=0',
                     '# profile_barrier=ready;wave=2;alive_disposed_peers=0',
                     '# profile_barrier=disposed;wave=2;alive_disposed_peers=1'):
            with self.assertRaises(ValueError):
                profile.barrier(line, 2)

    def test_alternate_zone_columns_are_retained_without_false_labels(self):
        text = 'MALLOC ZONE SIZE\nTOTAL 40.0M 15.0M 15.0M 0K 20,000 8000K 7000K 12\n'
        result = profile.native_summary(text)
        self.assertEqual(result['total_columns'][-1], '12')
        self.assertNotIn('allocated', result)

    def test_native_aggregate_excludes_process_details(self):
        text = 'Process: private/path [123]\nMALLOC ZONE   SIZE\nTOTAL 50.0M 15.0M 15.0M 0K 20000 8000K 7000K 46% 17\n'
        result = profile.native_summary(text)
        self.assertEqual(result['allocated'], '8000K')
        self.assertEqual(result['resident'], '15.0M')
        self.assertNotIn('private', str(result))
        with self.assertRaises(ValueError):
            profile.native_summary(text.replace('8000K', 'private/path'))
        with self.assertRaises(ValueError):
            profile.native_summary('TOTAL 50.0M 15.0M')


if __name__ == '__main__':
    unittest.main()
