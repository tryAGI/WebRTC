import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('symbols', Path(__file__).with_name('prepare-native-symbols.py'))
symbols = importlib.util.module_from_spec(spec); spec.loader.exec_module(symbols)


class ImportedModuleTests(unittest.TestCase):
    def test_real_signature_shape_and_non_module_units(self):
        text = 'DW_TAG_compile_unit\n DW_AT_name ("own.swift")\n'
        for name, identity in symbols.EXPECTED.items():
            text += f'DW_TAG_compile_unit\n DW_AT_name ("{name}")\n DW_AT_GNU_dwo_id (0x{identity})\n'
        self.assertEqual(symbols.units(text), symbols.EXPECTED)
        self.assertNotEqual(symbols.units(text.replace('6baccd36ecae4f32', '0000000000000000')), symbols.EXPECTED)

    def test_duplicate_signature_cannot_shadow_import(self):
        text = 'DW_TAG_compile_unit\n DW_AT_name ("Foundation")\n DW_AT_GNU_dwo_id (0x1234)\n'
        with self.assertRaises(ValueError): symbols.units(text + text)

    def test_missing_signature_is_not_accepted(self):
        self.assertNotEqual(symbols.units('DW_TAG_compile_unit\n DW_AT_name ("Foundation")'), symbols.EXPECTED)


if __name__ == '__main__': unittest.main()
