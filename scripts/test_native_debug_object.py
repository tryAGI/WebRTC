import importlib.util
from pathlib import Path
import struct
import unittest
spec = importlib.util.spec_from_file_location('debug_object', Path(__file__).with_name('verify-native-debug-object.py'))
debug = importlib.util.module_from_spec(spec); spec.loader.exec_module(debug)


def fixture(code=b'abcd', dwarf=b'efgh'):
    section_command_size = 72 + 160
    payload_start = 32 + section_command_size + 24
    header = struct.pack('<IiiIIIII', 0xfeedfacf, 0x100000c, 0, 1, 2, section_command_size + 24, 0, 0)
    segment = struct.pack('<II16sQQQQiiII', 0x19, section_command_size, b'', 0, 8, payload_start, 8, 7, 7, 2, 0)
    def section(name, segment, address, offset):
        return struct.pack('<16s16sQQIIIIIIII', name, segment, address, 4, offset, 2, 0, 0, 0, 0, 0, 0)
    sections = section(b'__text', b'__TEXT', 0, payload_start) + section(b'__debug_info', b'__DWARF', 4, payload_start + 4)
    symbols = struct.pack('<IIIIII', 2, 24, payload_start + 8, 0, payload_start + 8, 1)
    return header + segment + sections + symbols + code + dwarf + b'\0'


class DebugObjectTests(unittest.TestCase):
    def test_only_debug_changes_are_equivalent(self):
        self.assertTrue(debug.verify(fixture(), fixture(dwarf=b'zzzz'))['code_data_layout_relocations_symbols_equal'])

    def test_code_changes_are_rejected(self):
        with self.assertRaises(debug.ObjectMismatch): debug.verify(fixture(), fixture(code=b'zzzz'))

    def test_wrong_architecture_and_truncation_are_rejected(self):
        data = bytearray(fixture()); struct.pack_into('<i', data, 4, 7)
        for value in (bytes(data), fixture()[:20], fixture()[:-1]):
            with self.assertRaises(ValueError): debug.signature(value)



shadow_spec = importlib.util.spec_from_file_location('debug_shadow', Path(__file__).with_name('prepare-native-debug-shadow.py'))
shadow = importlib.util.module_from_spec(shadow_spec); shadow_spec.loader.exec_module(shadow)


def member(name, payload):
    header = name.encode().ljust(16) + b'0'.ljust(12) + b'0'.ljust(6) + b'0'.ljust(6) + b'644'.ljust(8) + str(len(payload)).encode().ljust(10) + b'`\n'
    return header + payload + (b'\n' if len(payload) & 1 else b'')


class ShadowArchiveTests(unittest.TestCase):
    def test_preserves_all_earlier_members_and_offsets(self):
        prefix = b'!<arch>\n' + member('index/', b'symbols') + member('other.o/', b'code')
        name = b'pal_swiftbindings.o\0\0'; last = member('#1/' + str(len(name)), name + b'old')
        result = shadow.replace_last_member(prefix + last, b'new object')
        self.assertTrue(result.startswith(prefix))
        payload = name + b'new object'
        self.assertTrue(result.endswith(payload + (b'\n' if len(payload) & 1 else b'')))

    def test_rejects_reordered_duplicate_or_truncated_archive(self):
        name = b'pal_swiftbindings.o'; last = member('#1/' + str(len(name)), name + b'old')
        for value in (b'invalid', b'!<arch>\n' + last + member('after.o/', b'code'), b'!<arch>\n' + last + last, (b'!<arch>\n' + last)[:-2]):
            with self.assertRaises(ValueError): shadow.replace_last_member(value, b'new')


if __name__ == '__main__': unittest.main()
