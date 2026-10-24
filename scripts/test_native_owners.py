import copy
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


if __name__ == '__main__': unittest.main()
