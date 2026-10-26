import importlib.util
import itertools
from pathlib import Path
import unittest
spec=importlib.util.spec_from_file_location('timer_control',Path(__file__).with_name('check-timer-control.py'))
control=importlib.util.module_from_spec(spec);spec.loader.exec_module(control)


class TimerControlTests(unittest.TestCase):
    def rows(self):
        return [dict(timer=kind,sample=str(i),requested_us='20000',elapsed_us='21000') for kind,i in itertools.product(('delay','periodic','sleep'),range(100))]

    def test_complete_slow_control_is_observed_without_false_acceptance(self):
        rows=self.rows();rows[0]['elapsed_us']='180000'
        self.assertEqual(control.validate(rows)['modes']['delay']['gaps_over_40ms'],1)

    def test_incomplete_duplicate_nonfinite_and_wrong_request_fail(self):
        rows=self.rows()
        for bad in (rows[:-1],rows+[rows[0]],rows[:-1]+[dict(rows[-1],elapsed_us='nan')],rows[:-1]+[dict(rows[-1],requested_us='10000')]):
            with self.assertRaises(ValueError): control.validate(bad)


if __name__=='__main__':unittest.main()
