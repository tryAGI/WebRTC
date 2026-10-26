import importlib.util
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest
spec=importlib.util.spec_from_file_location('qos',Path(__file__).with_name('check-native-qos.py'))
qos=importlib.util.module_from_spec(spec);spec.loader.exec_module(qos)


def capture():
    header='# native_source=abc\n# native_activity=off\n# native_qos_requested=default;observed=17\ntimer,sample,requested_us,elapsed_us,wall_elapsed_us,monotonic_elapsed_us\n'
    return header+'\n'.join(f'native-sleep,{i},20000,100000,100000,100000' for i in range(100))


class NativeQosTests(unittest.TestCase):
    def test_slow_complete_control_is_observed(self):
        with TemporaryDirectory() as d:
            p=Path(d)/'capture.csv';p.write_text(capture());source,proof=qos.read(p,'default')
            self.assertEqual(proof['median_raw_us'],100000);qos.activity_identity(p,'off')

    def test_duplicate_incomplete_nonfinite_wrong_qos_fail(self):
        good=capture();lines=good.splitlines()
        for bad in ('\n'.join(lines[:-1]),good+'\n'+lines[-1],good.replace(',100000,100000,100000',',nan,100000,100000'),good.replace('requested=default','requested=user-initiated')):
            with TemporaryDirectory() as d:
                p=Path(d)/'capture.csv';p.write_text(bad)
                with self.assertRaises(ValueError):qos.read(p,'default')

    def test_active_or_ambiguous_baseline_cannot_masquerade_as_default(self):
        for bad in (capture().replace('activity=off','activity=latency-critical'),capture()+'\n# native_activity=off',capture().replace('# native_activity=off\n','')):
            with TemporaryDirectory() as d:
                p=Path(d)/'capture.csv';p.write_text(bad)
                with self.assertRaises(ValueError):qos.activity_identity(p,'off')


if __name__=='__main__':unittest.main()
