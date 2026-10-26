import copy
import csv
import json
import tempfile
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('owners', Path(__file__).with_name('summarize-native-owners.py'))
owners = importlib.util.module_from_spec(spec); spec.loader.exec_module(owners)


def fixture():
    rows = [dict(phase=phase, wave=str(wave), mode='peer_open', trace='False', alive_disposed_peers='0',
                 rss='100', managed_committed='20', threadpool_threads='2', threadpool_pending='0', threadpool_completed='120')
            for phase, wave in [('disposed', w) for w in range(64)] + owners.PHASES[-2:]]
    samples = [dict(phase=phase, wave=wave, alive_disposed_peers=0,
                   malloc=dict(total_source='single_zone', resident='10M', allocated='1M'),
                   native_owners=dict(records=1, groups={'malloc_request/dotnet_runtime': dict(calls=1, requested_bytes=123)}))
               for phase, wave in owners.PHASES]
    data = dict(mode='peer_open', trace=0, waves=64, child_exit=0, owners=True, tiering_off=True, samples=samples)
    return data, rows


class NativeOwnersTests(unittest.TestCase):
    def test_exact_phases_and_numeric_fields(self):
        data, rows = fixture()
        phases = owners.analyze(data, rows, 'peer_open')
        self.assertEqual(len(phases), 6)
        self.assertEqual(phases[-1]['threadpool_pending'], 0)
        self.assertEqual(phases[0]['native_requests']['malloc_request/dotnet_runtime']['requested_bytes'], 123)

    def test_reject_missing_duplicate_phase_or_root(self):
        data, rows = fixture()
        variants = [rows[:-1], rows + [rows[-1]], copy.deepcopy(rows)]
        variants[-1][0]['alive_disposed_peers'] = '1'
        for variant in variants:
            with self.assertRaises(ValueError): owners.analyze(data, variant, 'peer_open')
        data['samples'][-1]['phase'] = 'disposed'
        with self.assertRaises(ValueError): owners.analyze(data, rows, 'peer_open')

    def test_no_uninstrumented_or_unmatched_cohort(self):
        for key, value in [('owners', False), ('tiering_off', False), ('child_exit', 1), ('mode', 'empty_control')]:
            data, rows = fixture(); data[key] = value
            with self.assertRaises(ValueError): owners.analyze(data, rows, 'peer_open')

    def test_reject_unknown_owner_negative_and_unbounded_fields(self):
        for group in ({'/private/path': dict(calls=1, requested_bytes=2)},
                      {'malloc_request/dotnet_runtime': dict(calls=-1, requested_bytes=2)},
                      {'malloc_request/dotnet_runtime': dict(calls=True, requested_bytes=2)},
                      {'malloc_request/dotnet_runtime': dict(calls=1, requested_bytes=2, stack='private')}):
            data, rows = fixture(); data['samples'][0]['native_owners']['groups'] = group
            with self.assertRaises(ValueError): owners.analyze(data, rows, 'peer_open')
        data, rows = fixture(); rows[2]['threadpool_threads'] = '-1'
        with self.assertRaises(ValueError): owners.analyze(data, rows, 'peer_open')


class NativeCohortTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup); self.root = Path(self.temp.name)
        source = 'a' * 40
        for mode in ('empty_control', 'peer_open'):
            for repetition in (1, 2, 3):
                directory = self.root / mode / str(repetition); directory.mkdir(parents=True)
                data, rows = fixture(); data['mode'] = mode
                for row in rows: row['mode'] = mode
                (directory / 'profile.json').write_text(json.dumps(data))
                with (directory / 'retention.csv').open('w') as stream:
                    stream.write(f'.NET 10.0.13; macOS 26.6.2; Arm64\ncpu_count=3; server_gc=False; dynamic_code=True; tiered_compilation=0\nlibrary_package=0.2.10-dev.43; library_source={source}\nharness_source={source}\n')
                    writer = csv.DictWriter(stream, ['mode'] + [key for key in rows[0] if key != 'mode']); writer.writeheader(); writer.writerows(rows)

    def test_complete_control_pair_is_descriptive(self):
        result = owners.summarize(self.root)
        self.assertFalse(result['leak_freedom_verdict'])
        self.assertEqual(result['workloads']['peer_open']['median_quiet_change']['rss'], 0)

    def test_missing_repetition_and_hardware_or_source_mismatch_fail(self):
        path = self.root / 'peer_open' / '3' / 'retention.csv'; original = path.read_text()
        for changed in (original.replace('cpu_count=3', 'cpu_count=5'), original.replace('a' * 40, 'b' * 40)):
            path.write_text(changed)
            with self.assertRaisesRegex(ValueError, 'Mixed instrumented'): owners.summarize(self.root)
        path.write_text(original)
        (path.parent / 'profile.json').unlink()
        with self.assertRaisesRegex(ValueError, 'Exactly three'): owners.summarize(self.root)

    def test_instrumented_quiescence_cannot_borrow_ordinary_memory_cohort(self):
        with self.assertRaisesRegex(ValueError, 'Incomplete'):
            owners.retention.summarize(self.root / 'peer_open', 'peer_open')


if __name__ == '__main__': unittest.main()
