import importlib.util
import itertools
from pathlib import Path
import unittest
spec=importlib.util.spec_from_file_location('clocks',Path(__file__).with_name('check-clock-isolation.py'))
clocks=importlib.util.module_from_spec(spec);spec.loader.exec_module(clocks)


class ClockTests(unittest.TestCase):
    def data(self):
        native=[dict(timer='native-sleep',sample=str(i),requested_us='20000',elapsed_us='120000',wall_elapsed_us='120000',monotonic_elapsed_us='120000') for i in range(100)]
        managed=[dict(timer=kind,sample=str(i),requested_us='20000',elapsed_us='120000',wall_elapsed_us='120000') for kind,i in itertools.product(('delay','periodic','sleep'),range(100))]
        return native,managed

    def test_slow_real_wake_and_clock_scaling_are_distinct_observations(self):
        native,managed=self.data();result=clocks.summarize(native,managed)
        self.assertEqual(result['clocks']['native-sleep']['median_wall_to_raw_ratio'],1)
        for row in managed:row['wall_elapsed_us']='20000'
        self.assertAlmostEqual(clocks.summarize(native,managed)['clocks']['delay']['median_wall_to_raw_ratio'],1/6)

    def test_missing_duplicate_and_nonfinite_are_rejected(self):
        native,managed=self.data()
        for bad in (native[:-1],native[:-1]+[native[0]],native[:-1]+[dict(native[-1],wall_elapsed_us='nan')]):
            with self.assertRaises(ValueError):clocks.summarize(bad,managed)
        with self.assertRaises(ValueError):clocks.summarize(native,managed[:-1])


if __name__=='__main__':unittest.main()
