import importlib.util
from pathlib import Path
import unittest
spec=importlib.util.spec_from_file_location('physical',Path(__file__).with_name('run-physical-scheduling.py'))
physical=importlib.util.module_from_spec(spec);spec.loader.exec_module(physical)


class PhysicalPolicyTests(unittest.TestCase):
    def test_verified_state_changes_and_unchanged_inheritance(self):
        for mode,before,after in (('inherited',1,1),('inherited',0,0),('background',0,1),('restored',1,0)):
            self.assertEqual(physical.policy(f'# scheduling_policy={mode};before={before};after={after}\n',mode)['after'],after)

    def test_wrong_missing_and_contaminated_policy_cannot_pass(self):
        for text in ('','# scheduling_policy=restored;before=1;after=1\n','# scheduling_policy=restored;before=1;after=0\nwarning: native failure\n'):
            with self.assertRaises(ValueError):physical.policy(text,'restored')
