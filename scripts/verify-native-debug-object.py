#!/usr/bin/env python3
"""Compare every nondebug Mach-O object section, relocation and symbol before debug-only use."""
import argparse
import hashlib
import json
from pathlib import Path
import struct


class ObjectMismatch(ValueError):
    def __init__(self, message, differences=None):
        super().__init__(message); self.differences = differences


def signature(data):
    def unpack(fmt, offset):
        if offset < 0 or offset + struct.calcsize(fmt) > len(data): raise ValueError('Truncated Mach-O structure')
        return struct.unpack_from(fmt, data, offset)
    def block(offset, size):
        if min(offset, size) < 0 or offset + size > len(data): raise ValueError('Truncated Mach-O data')
        return data[offset:offset + size]
    def label(value): return value.split(b'\0', 1)[0].decode('ascii')
    if len(data) > 64 * 1024 * 1024: raise ValueError('Object exceeds bound')
    magic, cpu, subtype, filetype, commands, command_bytes, flags, reserved = unpack('<IiiIIIII', 0)
    if magic != 0xfeedfacf or cpu != 0x100000c or filetype != 1 or commands > 64 or reserved != 0:
        raise ValueError('Expected bounded little-endian arm64 Mach-O object')
    offset = 32; sections = []; symtab = None; other = []
    for _ in range(commands):
        command, size = unpack('<II', offset)
        if size < 8 or offset + size > 32 + command_bytes: raise ValueError('Invalid load-command bound')
        raw = block(offset, size)
        if command == 0x19:
            fields = unpack('<II16sQQQQiiII', offset); count = fields[-2]
            if size != 72 + 80 * count or count > 128: raise ValueError('Invalid section count')
            for n in range(count):
                name, segment, address, length, location, alignment, relocations, relocation_count, section_flags, r1, r2, r3 = unpack('<16s16sQQIIIIIIII', offset + 72 + n * 80)
                if relocation_count > 100000: raise ValueError('Excessive relocation count')
                sections.append(dict(name=label(segment) + '/' + label(name), address=address, size=length, location=location,
                                     alignment=alignment, relocation_offset=relocations, relocation_count=relocation_count,
                                     flags=section_flags, reserved=[r1, r2, r3]))
        elif command == 2:
            if symtab is not None or size != 24: raise ValueError('Invalid/duplicate symbol table')
            symtab = unpack('<IIII', offset + 8)
        elif command in (0x26, 0x29, 0x2e):
            if size != 16: raise ValueError('Invalid linkedit data command')
            location, length = unpack('<II', offset + 8)
            other.append(dict(command=command, size=length, sha256=hashlib.sha256(block(location, length)).hexdigest()))
        elif command != 0xb: other.append(raw.hex()) # DYSYMTAB indexes are normalized through symbols below.
        offset += size
    if offset != 32 + command_bytes or symtab is None: raise ValueError('Incomplete Mach-O commands')
    symbol_offset, count, strings_offset, strings_size = symtab
    if count > 100000: raise ValueError('Excessive symbol table')
    strings = block(strings_offset, strings_size); symbols = []
    for index in range(count):
        string_index, kind, section_index, description, value = unpack('<IBBHQ', symbol_offset + index * 16)
        if string_index >= len(strings) or section_index > len(sections): raise ValueError('Invalid symbol index')
        end = strings.find(b'\0', string_index)
        if end < 0: raise ValueError('Unterminated symbol')
        name = strings[string_index:end].decode('utf-8')
        section_name = sections[section_index - 1]['name'] if section_index else ''
        symbols.append((name, kind, section_name, description, value))
    result = []
    for section in sections:
        name = section['name']
        if name.startswith('__DWARF/'): continue
        # This compiler-generated module hash is not linked in place of runtime
        # data: the candidate object is ONLY a dsymutil input. Its sole role is
        # module-debug identity; loaded code/data/section addresses must match.
        if name == '__LLVM/__swift_modhash':
            if section['size'] != 16 or section['relocation_count']: raise ValueError('Unexpected Swift module-hash layout')
            continue
        if name.startswith('__LLVM/'): raise ValueError('Unreviewed LLVM object section')
        relocations = []
        for i in range(section['relocation_count']):
            address, word = unpack('<iI', section['relocation_offset'] + i * 8)
            symbol = word & 0xffffff; external = (word >> 27) & 1; kind = word >> 28
            if kind == 10: target = ('addend', symbol - 0x1000000 if symbol & 0x800000 else symbol)
            elif external:
                if symbol >= len(symbols): raise ValueError('Relocation symbol index invalid')
                target = symbols[symbol]
            else:
                if not 0 < symbol <= len(sections): raise ValueError('Relocation section index invalid')
                target = ('section', sections[symbol - 1]['name'])
            relocations.append((address, (word >> 24) & 7, external, kind, target))
        # Mach-O zerofill sections have no file bytes; their declared size/layout
        # still participates in equality. Every other section is byte-exact.
        zero_fill = section['flags'] & 0xff in (1, 0xc, 0x12)
        payload = b'' if zero_fill else block(section['location'], section['size'])
        result.append(dict(name=name, address=section['address'], size=section['size'], alignment=section['alignment'],
                           flags=section['flags'], reserved=section['reserved'], sha256=hashlib.sha256(payload).hexdigest(), relocations=relocations))
    nondebug_symbols = [s for s in symbols if not s[1] & 0xe0 and not s[2].startswith(('__DWARF/', '__LLVM/'))]
    return dict(cpu=cpu, subtype=subtype, flags=flags, other_commands=other, sections=result, symbols=sorted(nondebug_symbols))


def verify(original, candidate):
    left, right = signature(original), signature(candidate)
    if left != right:
        # Public failure proof contains names/booleans only, never raw bytes,
        # relocated addresses or native symbol strings.
        changed = [k for k in left if left[k] != right[k]]
        left_sections = {s['name']: s for s in left['sections']}; right_sections = {s['name']: s for s in right['sections']}
        delta = {}
        for name in sorted(set(left_sections) | set(right_sections)):
            a, b = left_sections.get(name), right_sections.get(name)
            if a != b:
                delta[name] = dict(present_in_both=a is not None and b is not None)
                if a and b:
                    delta[name].update(original_size=a['size'], candidate_size=b['size'],
                        differing_fields=[key for key in a if a[key] != b[key]])
        differences = dict(fields=changed, section_differences=delta, original_symbols=len(left['symbols']), candidate_symbols=len(right['symbols']),
            symbol_names_equal={s[0] for s in left['symbols']} == {s[0] for s in right['symbols']},
            original_command_kinds=[s['command'] if isinstance(s, dict) else int.from_bytes(bytes.fromhex(s)[:4], 'little') for s in left['other_commands']],
            candidate_command_kinds=[s['command'] if isinstance(s, dict) else int.from_bytes(bytes.fromhex(s)[:4], 'little') for s in right['other_commands']])
        raise ObjectMismatch('Nondebug object content differs: ' + ','.join(changed), differences)
    digest = hashlib.sha256(json.dumps(left, sort_keys=True).encode()).hexdigest()
    return dict(nondebug_signature_sha256=digest, sections=len(left['sections']), symbols=len(left['symbols']),
                code_data_layout_relocations_symbols_equal=True, purpose='debug-only dsymutil input; never a runtime/linker substitute')


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__); p.add_argument('original', type=Path); p.add_argument('candidate', type=Path); p.add_argument('--output', required=True)
    args = p.parse_args(); Path(args.output).write_text(json.dumps(verify(args.original.read_bytes(), args.candidate.read_bytes()), indent=2) + '\n')
