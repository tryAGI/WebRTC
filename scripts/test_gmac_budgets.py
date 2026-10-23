import csv
import importlib.util
import itertools
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('gmac', Path(__file__).with_name('check-gmac-budgets.py'))
gmac = importlib.util.module_from_spec(spec); spec.loader.exec_module(gmac)
SOURCE = 'a' * 40


class GmacBudgets(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup); self.root = Path(self.temp.name)
        self.rows = [dict(profile=p, packet=packet, packet_bytes=gmac.PACKETS[packet], mode=mode, sample=sample,
                          operations=1000, managed_total_bytes=0, process_total_bytes=0, ns_per_op=100, cpu_ms=0, gen0=0, gen1=0, gen2=0)
                     for (p, packet, mode), sample in itertools.product(gmac.GROUPS, range(7))]
        for i in (1, 2, 3): self.write(i)

    def write(self, i, rows=None, source=SOURCE):
        rows = self.rows if rows is None else rows
        with (self.root / f'gmac-{i}.csv').open('w') as stream:
            stream.write(f'.NET 10.0.13; Ubuntu 24.04.5 LTS; X64\ncpu_count=4; server_gc=False; stopwatch_hz=1000000000; dynamic_code=True\nlibrary_package=0.2.10-dev.34; library_source={source}\nharness_source={source}\n')
            writer = csv.DictWriter(stream, rows[0]); writer.writeheader(); writer.writerows(rows)

    def test_exact_zero_and_capture_are_distinct(self):
        self.assertEqual(gmac.check(self.root, True)['samples'], 378)
        self.rows[0]['managed_total_bytes'] = 1; self.write(1)
        self.assertEqual(gmac.check(self.root)['verdict'], 'capture_valid')
        with self.assertRaises(ValueError): gmac.check(self.root, True)

    def test_replay_one_byte_and_nonfinite_fail_even_capture(self):
        next(r for r in self.rows if r['mode'] == 'replay')['managed_total_bytes'] = 1; self.write(1)
        with self.assertRaises(ValueError): gmac.check(self.root)
        next(r for r in self.rows if r['mode'] == 'replay')['managed_total_bytes'] = 0
        self.rows[0]['cpu_ms'] = 'nan'; self.write(1)
        with self.assertRaises(ValueError): gmac.check(self.root)

    def test_missing_duplicate_and_wrong_identity_fail(self):
        for rows in (self.rows[:-1], self.rows[:-1] + [self.rows[0]]):
            self.write(1, rows)
            with self.assertRaises(ValueError): gmac.check(self.root)
        self.write(1, source='b' * 40)
        with self.assertRaises(ValueError): gmac.check(self.root)


if __name__ == '__main__': unittest.main()
