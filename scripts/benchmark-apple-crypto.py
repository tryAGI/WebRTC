#!/usr/bin/env python3
"""Job-owned A/B of unchanged Apple bridge source; no SDK/runtime substitution."""
import argparse
import csv
import hashlib
import itertools
import json
from pathlib import Path
import statistics
import subprocess

SOURCE_SHA = '0ae3e543906cb338abac4c0628a8883dee87d7c8d77b8b756548a2cc2dc0231d'


def validate(text, optimization):
    rows = list(csv.DictReader(text.splitlines()))
    expected = set(itertools.product((16, 32), (0, 60, 1200), ('valid', 'bad_tag'), range(7)))
    seen = set()
    for row in rows:
        key = (int(row['key_bytes']), int(row['payload_bytes']), row['mode'], int(row['sample']))
        if key not in expected or key in seen or row['optimization'] != optimization: raise ValueError('Bridge coverage/source mode mismatch')
        seen.add(key)
        ns = float(row['ns_per_op'])
        if not 0 < ns < float('inf') or int(row['operations']) != 5000: raise ValueError('Invalid bridge timing/count')
        if (int(row['accepted']), int(row['rejected'])) != ((5000, 0) if key[2] == 'valid' else (0, 5000)): raise ValueError('Authentication/integrity control failed')
    if seen != expected: raise ValueError('Incomplete bridge cohort')
    return rows


def run(output):
    output.mkdir(parents=True, exist_ok=False)
    root = Path(__file__).resolve().parent.parent
    bridge = root / 'tests/diagnostics/NativeSymbols/dotnet/pal_swiftbindings.swift'
    driver = root / 'tests/diagnostics/AppleCrypto/Driver.swift'
    if hashlib.sha256(bridge.read_bytes()).hexdigest() != SOURCE_SHA: raise ValueError('Upstream bridge source changed')
    def command(args):
        # Forward complete diagnostics. Every compiler invocation treats warnings
        # as errors; no output filter or framework archive mutation is involved.
        subprocess.run(args, check=True, timeout=180)
    compiler = subprocess.run(['xcrun', 'swiftc', '--version'], check=True, capture_output=True, text=True, timeout=30).stdout
    sdk = subprocess.run(['xcrun', '--sdk', 'macosx', '--show-sdk-path'], check=True, capture_output=True, text=True, timeout=30).stdout.strip()
    common = ['xcrun', 'swiftc', '-warnings-as-errors', '-parse-as-library', '-sdk', sdk, '-target', 'arm64-apple-macosx12.0']
    driver_object = output / 'driver.o'
    command(common + ['-swift-version', '6', '-strict-concurrency=complete', '-O', '-emit-object', str(driver), '-o', str(driver_object)])
    for mode in ('Onone', 'O'):
        obj = output / (mode + '.o')
        command(common + ['-static', '-enable-library-evolution', '-runtime-compatibility-version', 'none', '-' + mode,
            '-module-name', 'pal_swiftbindings', '-emit-object', str(bridge), '-o', str(obj)])
        command(['xcrun', 'swiftc', '-warnings-as-errors', str(driver_object), str(obj), '-o', str(output / mode)])
    medians = {}
    for repetition in range(3):
        for mode in (('Onone', 'O') if repetition != 1 else ('O', 'Onone')):
            result = subprocess.run([str((output / mode).resolve()), mode], capture_output=True, text=True, check=True, timeout=120)
            if result.stderr: raise ValueError('Bridge workload emitted stderr: ' + result.stderr)
            (output / f'{mode}-{repetition}.csv').write_text(result.stdout)
            rows = validate(result.stdout, mode)
            for row in rows:
                key = '/'.join(row[k] for k in ('key_bytes', 'payload_bytes', 'mode'))
                medians.setdefault(mode, {}).setdefault(key, []).append(float(row['ns_per_op']))
    proof = dict(compiler=compiler, sdk=sdk, bridge_sha256=SOURCE_SHA,
        driver_object_sha256=hashlib.sha256(driver_object.read_bytes()).hexdigest(),
        scope='native bridge decryption and Swift errors only; no .NET exceptions/UDP; timings not allocation counters or a runtime replacement',
        processes_per_mode=3, samples_per_process=84,
        medians_ns={mode: {key: statistics.median(values) for key, values in cells.items()} for mode, cells in medians.items()})
    (output / 'proof.json').write_text(json.dumps(proof, indent=2) + '\n')


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__); p.add_argument('output', type=Path); args = p.parse_args(); run(args.output)
