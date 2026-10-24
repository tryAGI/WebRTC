import importlib.util
import itertools
from pathlib import Path
import unittest
spec = importlib.util.spec_from_file_location('bridge', Path(__file__).with_name('benchmark-apple-crypto.py'))
bridge = importlib.util.module_from_spec(spec); spec.loader.exec_module(bridge)


def capture():
    lines=['optimization,key_bytes,payload_bytes,mode,sample,operations,ns_per_op,accepted,rejected']
    for key,payload,mode,sample in itertools.product((16,32),(0,60,1200),('valid','bad_tag'),range(7)):
        lines.append(f'Onone,{key},{payload},{mode},{sample},5000,250.0,' + ('5000,0' if mode=='valid' else '0,5000'))
    return '\n'.join(lines)


class BridgeTests(unittest.TestCase):
    def test_complete_authentication_and_rejection_cohort(self):
        self.assertEqual(len(bridge.validate(capture(),'Onone')),84)

    def test_missing_duplicate_wrong_mode_and_accepted_corruption_fail(self):
        text=capture(); lines=text.splitlines()
        for bad in ('\n'.join(lines[:-1]), text+'\n'+lines[-1], text.replace('Onone','O'), text.replace(',0,5000',',1,4999'), text.replace(',250.0,',',nan,')):
            with self.assertRaises(ValueError): bridge.validate(bad,'Onone')


if __name__=='__main__': unittest.main()
