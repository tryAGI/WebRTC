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

    def test_single_zone_without_total_is_retained_and_redacted(self):
        text = 'MALLOC ZONE SIZE\nDefaultMallocZone_0x1234 50.0M 15.0M 15.0M 0K 20000 8000K 7000K 46% 17\n'
        result = profile.native_summary(text)
        self.assertEqual(result['total_source'], 'single_zone')
        self.assertEqual(result['allocated'], '8000K')
        self.assertNotIn('0x1234', str(result))
        multiple = text + 'OtherZone_0x5678 20M 10M 10M 0K 2000 1000K 1000K 50% 1\n'
        result = profile.native_summary(multiple)
        self.assertEqual(result['total_source'], 'zone_rows_without_total')
        self.assertEqual(len(result['zones']), 2)
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

    def test_quiescence_requires_explicit_phase(self):
        line = '# profile_barrier=quiescent_10;wave=63;alive_disposed_peers=0'
        self.assertEqual(profile.barrier(line, 63, 'quiescent_10'), 63)
        with self.assertRaises(ValueError): profile.barrier(line, 63)

    def test_native_owners_separate_vm_and_malloc_and_redact(self):
        text = ('Private /Users/private/path\n'
                '2 calls for 80 bytes: 0x123 (libcoreclr.dylib) Threadpool | 0x234 (libsystem_malloc.dylib) malloc\n'
                '1 call for 4096 bytes: 0x12 (libclrjit.dylib) Code | 0x3 (libsystem_kernel.dylib) mmap\n'
                '1 call for 128 bytes: 0x12 (CryptoKit) encrypt | 0x3 (libswiftCore.dylib) swift_allocObject | 0x4 (libsystem_malloc.dylib) malloc\n')
        result = profile.owner_summary(text)
        self.assertEqual(result['groups']['malloc_request/dotnet_runtime']['requested_bytes'], 80)
        self.assertEqual(result['groups']['vm_request/dotnet_jit']['requested_bytes'], 4096)
        self.assertEqual(result['groups']['malloc_request/crypto_sdk']['calls'], 1)
        self.assertNotIn('0x', str(result)); self.assertNotIn('private', str(result))
        self.assertEqual(profile.owner_summary('1 call for 16 bytes: unknown leaf')['groups']['unknown_request/unknown_or_other']['requested_bytes'], 16)
        for bad in ('', '1 call for bad bytes: malloc', '9' * 21 + ' calls for 1 bytes: malloc'):
            with self.assertRaises(ValueError): profile.owner_summary(bad)


if __name__ == '__main__':
    unittest.main()
