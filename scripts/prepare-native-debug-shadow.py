#!/usr/bin/env python3
"""Build code-identical debug-only input; leave the original runtime archive untouched."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import subprocess


def module(name, file):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(file))
    result = importlib.util.module_from_spec(spec); spec.loader.exec_module(result); return result


original = module('original_modules', 'prepare-native-symbols.py')
compare = module('object_compare', 'verify-native-debug-object.py')


def replace_last_member(archive, candidate):
    if not archive.startswith(b'!<arch>\n'): raise ValueError('Expected regular archive')
    offset = 8; members = []
    while offset < len(archive):
        header = archive[offset:offset + 60]
        if len(header) != 60 or header[58:] != b'`\n': raise ValueError('Invalid archive header')
        size = int(header[48:58]); name = header[:16].decode('ascii').strip()
        start = offset + 60; end = start + size
        if end > len(archive): raise ValueError('Truncated archive member')
        prefix = 0
        if name.startswith('#1/'):
            prefix = int(name[3:])
            if not 0 < prefix <= size: raise ValueError('Invalid extended member name')
            name = archive[start:start + prefix].rstrip(b'\0').decode('ascii')
        name = name.rstrip('/')
        members.append((offset, header, start, end, prefix, name))
        offset = end + (size & 1)
    if offset != len(archive) or not members or members[-1][-1] != 'pal_swiftbindings.o' or sum(m[-1] == 'pal_swiftbindings.o' for m in members) != 1:
        raise ValueError('Expected unique Swift object as final member; no symbol-index offsets may move')
    offset, header, start, end, prefix, name = members[-1]
    replacement = archive[start:start + prefix] + candidate
    new_header = header[:48] + str(len(replacement)).encode().ljust(10) + header[58:]
    return archive[:offset] + new_header + replacement + (b'\n' if len(replacement) & 1 else b'')


def prepare(archive, output):
    output.mkdir(parents=True, exist_ok=False)
    raw = archive.read_bytes()
    if hashlib.sha256(raw).hexdigest() != original.ARCHIVE_SHA: raise ValueError('Unreviewed official framework archive')
    compiler = original.checked(['xcrun', 'swiftc', '--version'])
    sdk = original.checked(['xcrun', '--sdk', 'macosx', '--show-sdk-path']).strip()
    if 'Swift version 6.1.2 ' not in compiler or not sdk.endswith('/MacOSX15.5.sdk'): raise ValueError('Exact original compiler/SDK required')
    source = Path(__file__).resolve().parent.parent / 'tests/diagnostics/NativeSymbols/dotnet/pal_swiftbindings.swift'
    if hashlib.sha256(source.read_bytes()).hexdigest() != '0ae3e543906cb338abac4c0628a8883dee87d7c8d77b8b756548a2cc2dc0231d': raise ValueError('Diagnostic source changed')
    old = output / 'official.o'; new = output / 'pal_swiftbindings.o'; cache = output / 'modules'
    old.write_bytes(subprocess.run(['ar', '-p', str(archive), 'pal_swiftbindings.o'], capture_output=True, check=True, timeout=20).stdout)
    original.checked(['xcrun', 'swiftc', '-emit-object', '-static', '-parse-as-library', '-enable-library-evolution', '-g', '-Onone',
        '-runtime-compatibility-version', 'none', '-sdk', sdk, '-target', 'arm64-apple-macosx12.0', '-module-name', 'pal_swiftbindings',
        '-module-cache-path', str(cache), str(source), '-o', str(new)])
    proof = compare.verify(old.read_bytes(), new.read_bytes())
    # Real code mutation must fail the same equivalence verifier. This is not a
    # missing-data or filename mismatch used as a surrogate positive control.
    mutated = bytearray(new.read_bytes()); offset = 32; changed = False
    for _ in range(struct.unpack_from('<I', mutated, 16)[0]):
        command, length = struct.unpack_from('<II', mutated, offset)
        if command == 0x19:
            count = struct.unpack_from('<I', mutated, offset + 64)[0]
            for i in range(count):
                section = offset + 72 + i * 80
                if mutated[section:section + 16].rstrip(b'\0') == b'__text':
                    location = struct.unpack_from('<I', mutated, section + 48)[0]
                    mutated[location] ^= 1; changed = True; break
        offset += length
        if changed: break
    if not changed: raise ValueError('Code-mutation control not exercised')
    try: compare.verify(old.read_bytes(), bytes(mutated))
    except compare.ObjectMismatch: proof['real_code_mutation_rejected'] = True
    else: raise ValueError('Equivalence verifier accepted changed code')
    # The NEW debug object references its own real generated PCM files. We never
    # map the original missing PCM to a mismatched module signature.
    imports = original.units(original.checked(['xcrun', 'dwarfdump', '--debug-info', '--recurse-depth=0', str(new)]))
    if set(imports) != set(original.EXPECTED): raise ValueError('Unexpected candidate module imports')
    for name, identity in imports.items():
        matches = [p for p in cache.rglob(name + '-*.pcm') if original.units(original.checked(['xcrun', 'dwarfdump', '--debug-info', '--recurse-depth=0', str(p)])).get(name) == identity]
        if len(matches) != 1: raise ValueError('Candidate debug import lacks exactly matching actual module data')
    shadow = output / archive.name
    shadow.write_bytes(replace_last_member(raw, new.read_bytes()))
    if subprocess.run(['ar', '-p', str(shadow), 'pal_swiftbindings.o'], capture_output=True, check=True, timeout=20).stdout != new.read_bytes():
        raise ValueError('Debug archive extraction mismatch')
    if archive.read_bytes() != raw: raise ValueError('Official runtime archive changed')
    options = '--object-prefix-map=' + str(archive.resolve()) + '=' + str(shadow.resolve())
    if any(c.isspace() for c in options): raise ValueError('Unsupported map path')
    proof.update(official_archive_sha256=original.ARCHIVE_SHA, candidate_module_signatures=imports, framework_modified=False,
                 linker_substitution=False, shadow_archive_sha256=hashlib.sha256(shadow.read_bytes()).hexdigest(),
                 scope='dsymutil debug-object relocation only; no linker/runtime archive substitution')
    (output / 'dsym-options.txt').write_text(options)
    (output / 'proof.json').write_text(json.dumps(proof, indent=2) + '\n')


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__); p.add_argument('archive', type=Path); p.add_argument('output', type=Path)
    args = p.parse_args()
    try: prepare(args.archive, args.output)
    except (ValueError, RuntimeError, subprocess.SubprocessError) as error:
        args.output.mkdir(parents=True, exist_ok=True)
        (args.output / 'failure-proof.json').write_text(json.dumps(dict(verdict='fail', reason=str(error)[:4096]), indent=2) + '\n')
        raise
