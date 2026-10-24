#!/usr/bin/env python3
"""Regenerate actual SDK module debug data; never edit framework archives or suppress diagnostics."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess

ARCHIVE_SHA = '56819aa278060142966ae19af836277d0a5a74fd0e53d9497aab44552d6fad67'
EXPECTED = {'Foundation': '6baccd36ecae4f32', '_SwiftConcurrencyShims': 'b8de9adb4227fa2e'}


def units(text):
    result = {}
    for block in text.split('DW_TAG_compile_unit')[1:]:
        name = re.search(r'DW_AT_name\s+\("([^"\n]+)"\)', block)
        signature = re.search(r'DW_AT_GNU_dwo_id\s+\(0x([0-9a-f]+)\)', block)
        if name and signature:
            if name[1] in result: raise ValueError('Duplicate module compilation unit')
            result[name[1]] = signature[1]
    return result


def checked(command, timeout=90):
    result = subprocess.run(command, capture_output=True, text=True, timeout=timeout)
    if len(result.stdout) + len(result.stderr) > 4 * 1024 * 1024: raise ValueError('Tool output exceeds bound')
    if result.returncode or re.search(r'\b(?:warning|error):', result.stdout + result.stderr, re.I):
        raise RuntimeError('Native tool failed or emitted a diagnostic: ' + result.stderr[:2048])
    return result.stdout


def prepare(archive, output, upstream_source=False):
    output.mkdir(parents=True, exist_ok=False)
    if hashlib.sha256(archive.read_bytes()).hexdigest() != ARCHIVE_SHA: raise ValueError('Unreviewed native framework archive')
    compiler = checked(['xcrun', 'swiftc', '--version'])
    sdk = str(Path(os.environ['DEVELOPER_DIR']) / 'Platforms/MacOSX.platform/Developer/SDKs/MacOSX15.5.sdk')
    sdk_version = checked(['xcrun', '--sdk', 'macosx', '--show-sdk-version']).strip()
    if 'Swift version 6.1.2 ' not in compiler or sdk_version != '15.5' or not Path(sdk).is_dir():
        raise ValueError('Exact framework compiler/SDK required: Swift6.1.2, macOS15.5')
    obj = output / 'original.o'
    result = subprocess.run(['ar', '-p', str(archive), 'pal_swiftbindings.o'], capture_output=True, timeout=20, check=True)
    obj.write_bytes(result.stdout)
    original = checked(['xcrun', 'dwarfdump', '--debug-info', '--recurse-depth=0', str(obj)])
    if units(original) != EXPECTED: raise ValueError('Original imported-module signatures changed')
    paths = re.findall(r'DW_AT_GNU_dwo_name\s+\("([^"\n]+)"\)', original)
    if len(paths) != 2: raise ValueError('Unexpected external module references')
    cache = output / 'modules'
    fixture_root = Path(__file__).resolve().parent.parent / 'tests/diagnostics/NativeSymbols'
    fixture = fixture_root / ('dotnet/pal_swiftbindings.swift' if upstream_source else 'ModuleProbe.swift')
    if upstream_source and hashlib.sha256(fixture.read_bytes()).hexdigest() != '0ae3e543906cb338abac4c0628a8883dee87d7c8d77b8b756548a2cc2dc0231d':
        raise ValueError('Pinned upstream diagnostic fixture changed')
    checked(['xcrun', 'swiftc', '-emit-object', '-static', '-parse-as-library', '-enable-library-evolution',
             '-g', '-O', '-runtime-compatibility-version', 'none', '-sdk', sdk, '-target', 'arm64-apple-macosx12.0',
             '-module-name', 'pal_swiftbindings', '-module-cache-path', str(cache), str(fixture), '-o', str(output / 'probe.o')])
    found, signatures = {}, {}
    mismatches = []
    for module, expected in EXPECTED.items():
        candidates = list(cache.rglob(module + '-*.pcm'))
        if not candidates or len(candidates) > 8: raise ValueError('Missing/excessive actual SDK module candidates')
        actual = []
        for path in candidates:
            metadata = checked(['xcrun', 'dwarfdump', '--debug-info', '--recurse-depth=0', str(path)])
            ids = units(metadata)
            signatures.setdefault(module, []).append(dict(ids=ids, cache_key=path.parent.name))
            if ids.get(module) == expected: actual.append(path)
        if len(actual) != 1:
            mismatches.append(module)
        else: found[module] = actual[0]
    if mismatches:
        (output / 'signature-mismatch.json').write_text(json.dumps(dict(fixture='upstream' if upstream_source else 'authored', modules=signatures), indent=2) + '\n')
        raise ValueError('Regenerated SDK module signature does not match original debug import: ' + ','.join(mismatches))
    maps = []
    for old in paths:
        name = Path(old).name.split('-', 1)[0]
        new = str(found[name].resolve())
        if not all(re.fullmatch(r'/[A-Za-z0-9_./-]+', p) for p in (old, new)): raise ValueError('Unsafe module map path')
        maps.append('--object-prefix-map=' + old + '=' + new)
    if hashlib.sha256(archive.read_bytes()).hexdigest() != ARCHIVE_SHA: raise ValueError('Framework archive changed')
    (output / 'dsym-options.txt').write_text(' '.join(maps))
    (output / 'proof.json').write_text(json.dumps(dict(archive_sha256=ARCHIVE_SHA, compiler=compiler.strip(), signatures=EXPECTED,
        module_data='actual SDK-generated PCM with matching imported DWARF signatures', framework_modified=False), indent=2) + '\n')


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__); p.add_argument('archive', type=Path); p.add_argument('output', type=Path)
    p.add_argument('--upstream-source', action='store_true')
    args = p.parse_args()
    try: prepare(args.archive, args.output, args.upstream_source)
    except (ValueError, RuntimeError, subprocess.SubprocessError) as error:
        args.output.mkdir(parents=True, exist_ok=True)
        (args.output / 'failure-proof.json').write_text(json.dumps(dict(verdict='fail', reason=str(error)[:4096]), indent=2) + '\n')
        raise
