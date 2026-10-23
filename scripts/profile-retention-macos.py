#!/usr/bin/env python3
"""Capture only an owned child's post-disposal barriers; never suspend/resume it."""
import argparse
import json
import os
import re
from pathlib import Path
import selectors
import subprocess
import sys
import time


def barrier(line, expected):
    if not line.startswith('# profile_barrier='):
        return None
    fields = dict(item.split('=', 1) for item in line[2:].split(';'))
    wave = int(fields['wave'])
    phase = 'ready' if expected == -5 else 'disposed'
    if wave != expected or fields['profile_barrier'] != phase or fields['alive_disposed_peers'] != '0':
        raise ValueError('Unexpected profiling phase, wave or live peer count')
    return wave


def native_summary(text):
    """Allowlist only aggregate numeric zone columns; retain no addresses/paths."""
    zone = text.rsplit('MALLOC ZONE', 1)
    if len(zone) != 2:
        raise ValueError('Native malloc zone summary missing')
    names = ('virtual', 'resident', 'dirty', 'swapped', 'allocations', 'allocated', 'fragmentation', 'fragmentation_percent', 'regions')
    lines = zone[1].splitlines()
    headings = [value.strip() for value in lines if value.strip() and re.fullmatch(r'[A-Z +%_/-]+', value.strip())]
    rows = []
    for line in lines:
        fields = line.split()
        # Zone identifiers can contain native addresses. Keep only a bounded
        # numeric suffix, never the identifier or an arbitrary raw line.
        values = []
        for value in reversed(fields):
            if not re.fullmatch(r'[0-9][0-9,]*(?:\.[0-9]+)?[BKMGT%]?', value):
                break
            values.append(value)
        values.reverse()
        if 5 <= len(values) <= 15:
            rows.append((fields[0] == 'TOTAL', values))
    if not rows or len(rows) > 64:
        raise ValueError('Native malloc numeric rows missing or excessive')
    totals = [values for total, values in rows if total]
    if len(totals) > 1:
        raise ValueError('Duplicate native malloc totals')
    result = dict(headings=headings, zones=[dict(ordinal=i, columns=values)
                  for i, (total, values) in enumerate(rows) if not total])
    values = totals[0] if totals else rows[0][1] if len(rows) == 1 else None
    if values is not None:
        result.update(total_columns=values, total_source='explicit_total' if totals else 'single_zone')
        if len(values) == 9 and values[7].endswith('%'):
            result.update(zip(names, values))
    else:
        result['total_source'] = 'zone_rows_without_total'
    return result


def run(binary, output, mode, trace, waves=24, tiering_off=False):
    if sys.platform != 'darwin':
        raise ValueError('This controller requires macOS vmmap')
    output.mkdir(parents=True, exist_ok=False)
    settings = dict(os.environ, TRYAGI_RETENTION_MODE=mode, TRYAGI_RETENTION_TRACE=str(trace), TRYAGI_RETENTION_PROFILE='1', TRYAGI_RETENTION_WAVES=str(waves))
    if tiering_off: settings['DOTNET_TieredCompilation'] = '0'
    expected = [-5, 2, 16, 32, 63] if waves == 64 else [-5, 2, 16]
    samples = []
    started = time.monotonic()
    command = (['dotnet', str(binary.resolve())] if binary.suffix == '.dll' else [str(binary.resolve())]) + ['--suite', 'retention']
    child = subprocess.Popen(command,
                             stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, env=settings)
    try:
        with selectors.DefaultSelector() as selector, (output / 'retention.csv').open('wb') as log:
            selector.register(child.stdout, selectors.EVENT_READ)
            pending = b''
            total = 0
            while selector.get_map():
                if time.monotonic() - started > (600 if waves == 64 else 210):
                    raise TimeoutError('Owned retention controller exceeded its deadline')
                for key, _ in selector.select(timeout=1):
                    data = os.read(key.fileobj.fileno(), 65536)
                    if not data:
                        selector.unregister(key.fileobj)
                        continue
                    total += len(data)
                    if total > 4 * 1024 * 1024:
                        raise ValueError('Retention output exceeds four MiB bound')
                    log.write(data)
                    log.flush()
                    pending += data
                    while b'\n' in pending:
                        raw, pending = pending.split(b'\n', 1)
                        line = raw.decode('utf-8').strip()
                        if not line.startswith('# profile_barrier='):
                            continue
                        if not expected:
                            raise ValueError('Duplicate or extra profiling barrier')
                        wave = barrier(line, expected[0])
                        expected.pop(0)
                        if wave != -5:
                            capture = subprocess.run(['/usr/bin/vmmap', '-summary', str(child.pid)],
                                                     capture_output=True, timeout=15)
                            (output / f'vmmap-wave-{wave}.txt').write_bytes(capture.stdout + capture.stderr)
                            if capture.returncode:
                                raise RuntimeError('Native memory capture failed')
                            samples.append(dict(wave=wave, phase='disposed', alive_disposed_peers=0,
                                                malloc=native_summary(capture.stdout.decode('utf-8')),
                                                elapsed_seconds=time.monotonic() - started))
                        child.stdin.write(f'continue:{wave}\n'.encode())
                        child.stdin.flush()
            if child.wait(timeout=10) != 0 or expected:
                raise RuntimeError('Retention child failed or omitted a profiling barrier')
        result = dict(mode=mode, trace=trace, waves=waves, tiering_off=tiering_off, samples=samples, child_exit=0,
                      scope='phase-matched post-disposal snapshots; profiling perturbs timing; no leak-freedom verdict')
        (output / 'profile.json').write_text(json.dumps(result, indent=2) + '\n')
        print(json.dumps(result))
    finally:
        if child.poll() is None:
            child.terminate()
            try:
                child.wait(timeout=10)
            except subprocess.TimeoutExpired:
                child.kill()
                child.wait(timeout=10)
        child.stdin.close()
        child.stdout.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('binary', type=Path)
    parser.add_argument('output', type=Path, help='New private output directory; raw vmmap contains process details')
    parser.add_argument('--mode', choices=('empty_control', 'identity_only', 'crypto_only', 'peer_audio', 'peer_open'), default='peer_open')
    parser.add_argument('--trace', type=int, choices=(0, 1), default=0)
    parser.add_argument('--waves', type=int, choices=(24, 64), default=24)
    parser.add_argument('--tiering-off', action='store_true', help='Owned-child diagnostic control only; ordinary budget readers reject this cohort')
    args = parser.parse_args()
    run(args.binary, args.output, args.mode, args.trace, args.waves, args.tiering_off)
