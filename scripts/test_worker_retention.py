import copy
import csv
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
spec = importlib.util.spec_from_file_location('workers', Path(__file__).with_name('summarize-worker-retention.py'))
workers = importlib.util.module_from_spec(spec); spec.loader.exec_module(workers)


def fixture(minimum, traced):
    rows = [dict(mode='peer_open', trace='False', phase=p, wave=str(w), alive_disposed_peers='0', threadpool_minimum=str(minimum or 3),
                 rss='1000', managed_committed='100', threadpool_threads=str(minimum or 2), threadpool_pending='0')
            for p, w in [('disposed', i) for i in range(24)] + workers.PHASES[-2:]]
    samples = [dict(phase=p, wave=w, alive_disposed_peers=0,
                   malloc=dict(total_source='single_zone', resident='10M', dirty='10M', allocated='1M', fragmentation='9M')) for p,w in workers.PHASES]
    if traced:
        for sample in samples: sample['native_owners'] = dict(groups={'unknown_request/dotnet_runtime': dict(calls=2, requested_bytes=1000)})
    return dict(mode='peer_open', trace=0, waves=24, child_exit=0, owners=traced, quiescence=True, worker_minimum=minimum, tiering_off=True, samples=samples), rows


class WorkerRetentionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup); self.root = Path(self.temp.name)
        for traced in (False, True):
            for minimum in (0, 24):
                for repetition in (1, 2, 3):
                    directory = self.root / (('traced' if traced else 'plain') + '-' + str(minimum)) / str(repetition)
                    directory.mkdir(parents=True); data, rows = fixture(minimum, traced)
                    (directory / 'profile.json').write_text(json.dumps(data))
                    with (directory / 'retention.csv').open('w') as stream:
                        source = 'a' * 40
                        stream.write(f'.NET 10.0.13; macOS 26.6.2; Arm64\ncpu_count=3; server_gc=False; dynamic_code=True; native_stack_logging={1 if traced else "off"}; tiered_compilation=0\nlibrary_package=test; library_source={source}\nharness_source={source}\n# configured_worker_minimum={minimum};quiescence=True\n')
                        writer = csv.DictWriter(stream, rows[0]); writer.writeheader(); writer.writerows(rows)

    def test_factorial_controls_are_separate_and_not_leak_acceptance(self):
        result = workers.summarize(self.root)
        self.assertFalse(result['leak_freedom_verdict'])
        self.assertEqual(result['minimum24_minus_default_quiet30']['plain']['threadpool_threads'], 22)

    def test_uninstrumented_and_worker_controls_cannot_borrow_budget(self):
        core = workers.native.core
        for control, message in (('plain-24', 'Worker minimum'), ('traced-0', 'Native stack logging')):
            path = self.root / control / '1' / 'retention.csv'
            original = path.read_text(); path.write_text(original.replace('tiered_compilation=0', 'tiered_compilation=default'))
            with self.assertRaisesRegex(ValueError, message): core.read_csv(path, 'mode')

    def test_mixed_hardware_source_and_missing_repetitions_fail(self):
        path = self.root / 'plain-24' / '1' / 'retention.csv'; original = path.read_text()
        for changed in (original.replace('cpu_count=3','cpu_count=5'), original.replace('a'*40,'b'*40)):
            path.write_text(changed)
            with self.assertRaisesRegex(ValueError, 'Mixed worker'): workers.summarize(self.root)
        path.write_text(original); (path.parent / 'profile.json').unlink()
        with self.assertRaisesRegex(ValueError, 'Three fresh'): workers.summarize(self.root)

    def test_rooted_pending_wrong_minimum_and_incomplete_phases_fail(self):
        for key,value in (('alive_disposed_peers','1'), ('threadpool_pending','1'), ('threadpool_minimum','2')):
            data,rows = fixture(0,False); rows[-1][key]=value
            with self.assertRaises(ValueError): workers.phases(data,rows,0,False)
        data,rows = fixture(24,True)
        with self.assertRaises(ValueError): workers.phases(data,rows[:-1],24,True)
        data['samples'][0]['phase']='quiescent_10'
        with self.assertRaises(ValueError): workers.phases(data,rows,24,True)

    def test_native_minimum_control_cannot_borrow_ordinary_owner_profile(self):
        spec = importlib.util.spec_from_file_location('owners','scripts/summarize-native-owners.py')
        owners=importlib.util.module_from_spec(spec); spec.loader.exec_module(owners)
        data,rows=fixture(24,True)
        with self.assertRaisesRegex(ValueError, 'own cohort'): owners.analyze(data,rows,'peer_open')


if __name__ == '__main__': unittest.main()
