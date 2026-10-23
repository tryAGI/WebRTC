import csv
import importlib.util
import itertools
import json
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
                metadata(stream, dynamic, source); writer = csv.DictWriter(stream, rows[0]); writer.writeheader(); writer.writerows(rows)

    def test_same_runner_ratio_is_descriptive(self):
        result = compare.compare(self.root)
        self.assertFalse(result['timing_gate'])
        self.assertTrue(all(r['aot_over_jit'] == 2 for r in result['timings'].values()))

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

    def test_native_size_precision_and_unknown_format(self):
        self.assertEqual(retention.size('1.5M'), 1572864)
        with self.assertRaises(ValueError): retention.size('private/path')


if __name__ == '__main__': unittest.main()
