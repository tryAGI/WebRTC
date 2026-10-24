#!/usr/bin/env python3
"""Check actual crypto/managed function ranges independently of full-DWARF validity."""
import argparse
import json
from pathlib import Path
import re
import subprocess

p = argparse.ArgumentParser(description=__doc__); p.add_argument('output'); args = p.parse_args()
locations = [Path('artifacts/smoke/WebRTC.AotSmoke.dSYM'), Path('src/tests/WebRTC.AotSmoke/bin/Release/net10.0/osx-arm64/native/WebRTC.AotSmoke.dSYM')]
existing = [path for path in locations if path.is_dir()]
if not existing: raise ValueError('Native symbol bundle missing')
proof = dict(scope='function-range usability only; full DWARF verification remains an independent mandatory gate')
for key, name in (('crypto', 'AesGcmDecrypt'), ('managed', 'ProcessDatagram')):
    result = subprocess.run(['xcrun', 'dwarfdump', '--debug-info', '--regex', '--name', name, str(existing[0])], capture_output=True, text=True, timeout=30)
    if result.stderr: print(result.stderr, end='')
    if result.returncode or re.search(r'\b(?:warning|error):', result.stderr, re.I): raise RuntimeError('Function range query failed or emitted a diagnostic')
    if len(result.stdout) > 4 * 1024 * 1024: raise ValueError('Range query exceeds bound')
    if name not in result.stdout or 'DW_TAG_subprogram' not in result.stdout or not any(attr in result.stdout for attr in ('DW_AT_low_pc', 'DW_AT_ranges')):
        raise ValueError('Usable function range missing: ' + name)
    proof[key + '_function_ranges_present'] = True
Path(args.output).write_text(json.dumps(proof, indent=2) + '\n')
