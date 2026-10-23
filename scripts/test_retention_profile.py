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


if __name__ == '__main__':
    unittest.main()
