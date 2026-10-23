import csv
import importlib.util
import itertools
import json
import shutil
from pathlib import Path
import tempfile
import unittest


def module(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    result = importlib.util.module_from_spec(spec); spec.loader.exec_module(result)
    return result


compare = module('comparison', 'compare-aot-performance.py')
retention = module('retention', 'summarize-native-retention.py')
SOURCE = 'a' * 40


def metadata(stream, dynamic, source=SOURCE):
    stream.write(f'.NET 10.0.13; macOS 26.6.2; Arm64\ncpu_count=3; server_gc=False; stopwatch_hz=1000000000; dynamic_code={dynamic}\nlibrary_package=0.2.10-dev.34; library_source={source}\nharness_source={source}\n')


class ComparisonTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup); self.root = Path(self.temp.name)
        self.write('jit', 'True'); self.write('aot', 'False')

    def write(self, mode, dynamic, source=SOURCE, allocation='0'):
        root = self.root / mode; root.mkdir(exist_ok=True)
        for crypto in (True, False):
            rows = [dict(profile=p, payload_bytes=length, sample=s, roundtrips=5000, managed_bytes_per_roundtrip='0.00', ns_per_roundtrip=100 if mode == 'jit' else 200, managed_total_bytes=allocation)
                    for p, length, s in itertools.product(compare.core.PROFILES, (60, 1200), range(7))] if crypto else [dict(operation=p, sample=s, operations=20000, managed_bytes_per_op='0.00', ns_per_op=10 if mode == 'jit' else 20, managed_total_bytes=allocation)
                    for p, s in itertools.product(compare.core.OPERATIONS, range(7))]
            with (root / ('performance.csv' if crypto else 'packets.csv')).open('w') as stream:
                metadata(stream, dynamic, source)
                keys = {r['profile'] + '/' + str(r['payload_bytes']) if crypto else r['operation'] for r in rows}
                for key in sorted(keys): stream.write(f'#warmup,{key},50,520,99,101,True\n')
                writer = csv.DictWriter(stream, rows[0]); writer.writeheader(); writer.writerows(rows)

    def test_same_runner_ratio_is_descriptive(self):
        result = compare.compare(self.root)
        self.assertFalse(result['timing_gate'])
        self.assertTrue(all(r['aot_over_jit'] == 2 for r in result['timings'].values()))

    def test_unstable_warmup_omits_ratio(self):
        path = self.root / 'jit' / 'packets.csv'
        path.write_text(path.read_text().replace(',True\n', ',False\n'))
        result = compare.compare(self.root)
        self.assertTrue(all(result['timings'][key]['aot_over_jit'] is None for key in compare.core.OPERATIONS))

    def test_deadline_before_five_batches_is_unstable_observation(self):
        path = self.root / 'jit' / 'packets.csv'
        path.write_text(path.read_text().replace(',50,520,99,101,True', ',3,3100,99,101,False'))
        result = compare.compare(self.root)
        self.assertTrue(all(result['timings'][key]['aot_over_jit'] is None for key in compare.core.OPERATIONS))

    def test_measurement_drift_omits_ratio_after_stable_warmup(self):
        path = self.root / 'jit' / 'packets.csv'
        text = path.read_text(); text = text.replace('20000,0.00,10,0', '20000,0.00,1000,0', 1); path.write_text(text)
        result = compare.compare(self.root)
        row = result['timings'][compare.core.OPERATIONS[0]]
        self.assertTrue(row['jit_warmup']['observed_stable'])
        self.assertFalse(row['timing_comparable']); self.assertIsNone(row['aot_over_jit'])

    def test_missing_duplicate_and_false_stability_fail(self):
        path = self.root / 'jit' / 'packets.csv'; original = path.read_text()
        record = next(line for line in original.splitlines() if line.startswith('#warmup,'))
        for changed in (original.replace(record + '\n', ''), original + record + '\n', original.replace(',520,99,101,True', ',20,99,101,True')):
            path.write_text(changed)
            with self.assertRaises(ValueError): compare.compare(self.root)

    def test_compiler_source_and_allocation_mismatches_fail(self):
        for args in (('True', SOURCE, '0'), ('False', 'b' * 40, '0'), ('False', SOURCE, '1')):
            self.write('aot', *args)
            with self.assertRaises(ValueError): compare.compare(self.root)


class RetentionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup); self.root = Path(self.temp.name)
        for i in (1, 2, 3): self.write(i)

    def write(self, i, rooted=False, missing=False):
        root = self.root / str(i); root.mkdir(exist_ok=True)
        samples = [dict(wave=w, phase='disposed', alive_disposed_peers=0, malloc=dict(total_source='single_zone', resident=f'{10 + w / 64}M', allocated='1000K', fragmentation='9000K')) for w in (2, 16, 32, 63)]
        (root / 'profile.json').write_text(json.dumps(dict(mode='crypto_only', trace=0, waves=64, child_exit=0, samples=samples)))
        rows = [dict(mode='crypto_only', trace='False', wave=w, phase='disposed', alive_disposed_peers=int(rooted), rss=1000000 + w, managed_committed=600000) for w in range(63 if missing else 64)]
        with (root / 'retention.csv').open('w') as stream:
            metadata(stream, 'True'); stream.write('# process metadata\n'); writer = csv.DictWriter(stream, rows[0]); writer.writeheader(); writer.writerows(rows)

    def test_three_process_ranges_are_not_leak_acceptance(self):
        result = retention.summarize(self.root, 'crypto_only')
        self.assertFalse(result['leak_freedom_verdict']); self.assertEqual(result['processes'], 3)
        self.assertEqual(result['delta_bytes']['managed_committed']['median'], 0)
        self.assertGreater(result['tail_bytes_per_wave']['malloc_resident']['median'], 0)

    def test_missing_or_rooted_waves_fail(self):
        for rooted, missing in ((True, False), (False, True)):
            self.write(3, rooted, missing)
            with self.assertRaises(ValueError): retention.summarize(self.root, 'crypto_only')

    def test_tiering_diagnostic_cannot_borrow_ordinary_budget_cohort(self):
        for directory in self.root.iterdir():
            path = directory / 'retention.csv'
            path.write_text(path.read_text().replace('dynamic_code=True', 'dynamic_code=True; tiered_compilation=0'))
        result = retention.summarize(self.root, 'crypto_only')
        self.assertTrue(result['cohort'].endswith('tiered_compilation=0'))
        with self.assertRaisesRegex(ValueError, 'Non-default tiering'):
            retention.core.read_csv(self.root / '1' / 'retention.csv', 'mode')

    def test_native_size_precision_and_unknown_format(self):
        self.assertEqual(retention.size('1.5M'), 1572864)
        with self.assertRaises(ValueError): retention.size('private/path')




class MemoryComparisonTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup); self.root = Path(self.temp.name)
        self.comparison = module('memory_comparison', 'compare-native-retention.py')
        for control in ('default', 'off'):
            builder = RetentionTests(); builder.temp = self.temp; builder.root = self.root / control / 'profiles'
            builder.root.mkdir(parents=True)
            for i in (1, 2, 3): builder.write(i)
            if control == 'off':
                for path in builder.root.glob('*/retention.csv'):
                    path.write_text(path.read_text().replace('dynamic_code=True', 'dynamic_code=True; tiered_compilation=0'))

    def test_same_source_controls_recomputed_without_owner_claim(self):
        result = self.comparison.compare(self.root, ('crypto_only',))
        self.assertFalse(result['leak_freedom_verdict'])
        self.assertEqual(result['workloads']['crypto_only']['off_minus_default_final_median_bytes']['malloc_resident'], 0)

    def test_cpu_count_can_differ_between_workloads_but_not_within_pairs(self):
        for control in ('default', 'off'):
            other = self.root / ('identity-' + control)
            shutil.copytree(self.root / control, other)
            for path in other.glob('profiles/*/profile.json'):
                data = json.loads(path.read_text()); data['mode'] = 'identity_only'; path.write_text(json.dumps(data))
            for path in other.glob('profiles/*/retention.csv'):
                path.write_text(path.read_text().replace('crypto_only', 'identity_only').replace('cpu_count=3', 'cpu_count=5'))
        result = self.comparison.compare(self.root, ('crypto_only', 'identity_only'))
        self.assertIn('cpu=3', result['workloads']['crypto_only']['cohort'])
        self.assertIn('cpu=5', result['workloads']['identity_only']['cohort'])
        for path in (self.root / 'off').glob('profiles/*/retention.csv'):
            path.write_text(path.read_text().replace('cpu_count=3', 'cpu_count=5'))
        with self.assertRaisesRegex(ValueError, 'within-workload'):
            self.comparison.compare(self.root, ('crypto_only', 'identity_only'))

    def test_missing_duplicate_mixed_source_and_wrong_tiering_fail(self):
        path = self.root / 'off' / 'profiles' / '1' / 'retention.csv'; original = path.read_text()
        for changed in (original.replace(SOURCE, 'b' * 40), original.replace('tiered_compilation=0', 'tiered_compilation=1'), original.replace('cpu_count=3', 'cpu_count=5')):
            path.write_text(changed)
            with self.assertRaises(ValueError): self.comparison.compare(self.root, ('crypto_only',))
        path.write_text(original)
        with self.assertRaises(ValueError): self.comparison.compare(self.root)
        shutil.copytree(self.root / 'default', self.root / 'duplicate')
        with self.assertRaisesRegex(ValueError, 'Duplicate'): self.comparison.compare(self.root, ('crypto_only',))


if __name__ == '__main__': unittest.main()
