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


def barrier(line, expected, phase=None):
    if not line.startswith('# profile_barrier='):
        return None
    fields = dict(item.split('=', 1) for item in line[2:].split(';'))
    wave = int(fields['wave'])
    phase = phase or ('ready' if expected == -5 else 'disposed')
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


def owner_summary(text):
    # allBySize emits requested sizes, including mmap reservations. These are
    # NOT resident bytes and VM/malloc groups must never be summed as footprint.
    groups = {}; records = 0
    for line in text.splitlines():
        found = re.fullmatch(r'(\d{1,20}) calls? for (\d{1,20}) bytes: (.+)', line.strip())
        if not found:
            if re.match(r'\d+ calls? for ', line.strip()): raise ValueError('Malformed native allocation record')
            continue
        records += 1; count, size = int(found[1]), int(found[2])
        frames = found[3].split(' | ')
        leaf = frames[-1].lower()
        kind = 'vm_request' if any(x in leaf for x in ('mmap', 'vm_allocate', 'vm_map')) else 'malloc_request' if any(x in leaf for x in ('malloc', 'calloc', 'realloc', 'memalign')) else 'unknown_request'
        owner = 'unknown_or_other'
        for frame in reversed(frames):
            name = frame.lower()
            if any(x in name for x in ('libsystem_malloc', 'libsystem_kernel', 'swift_allocobject', 'swift_slowalloc', 'swift_allocbox')): continue
            if 'libclrjit' in name: owner = 'dotnet_jit'
            elif 'libcoreclr' in name or 'libmonosgen' in name: owner = 'dotnet_runtime'
            elif any(x in name for x in ('cryptokit', 'applecryptonative_', 'cryptography.native.apple', '(security)')): owner = 'crypto_sdk'
            elif any(x in name for x in ('foundation', 'corefoundation')): owner = 'foundation_sdk'
            elif 'libswift' in name: owner = 'swift_runtime'
            elif any(x in name for x in ('system.native', 'libnetwork', 'network.framework')): owner = 'socket_sdk'
            else: continue
            break
        entry = groups.setdefault(kind + '/' + owner, dict(calls=0, requested_bytes=0))
        entry['calls'] += count; entry['requested_bytes'] += size
    if not records or records > 100000: raise ValueError('Native allocation stack records missing or excessive')
    return dict(records=records, groups=groups, basis='nearest recognized native nonallocator frame; managed callers may be unsymbolicated; requested sizes are not residency; VM/malloc can overlap; no causal leak verdict')


def owner_diagnostic(text):
    # A symbol such as mach_o::Error is stack data, not a tool warning.
    # Inspect every nonrecord line; malformed records still fail owner_summary.
    messages = '\n'.join(line for line in text.splitlines() if not re.match(r'\d+ calls? for ', line.strip()))
    return bool(re.search(r'\b(?:warning|error|cannot|unable)\b', messages, re.I))


def capture_owners(pid, path):
    # Bound both time and bytes while draining; terminate only this owned tool.
    command = ['/usr/bin/malloc_history', str(pid), '-allBySize']
    tool = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    data = bytearray(); start = time.monotonic()
    try:
        with selectors.DefaultSelector() as selected:
            selected.register(tool.stdout, selectors.EVENT_READ)
            while selected.get_map():
                if time.monotonic() - start > 30: raise TimeoutError('Native owner capture deadline')
                for key, _ in selected.select(timeout=1):
                    block = os.read(key.fileobj.fileno(), 65536)
                    if not block: selected.unregister(key.fileobj); continue
                    data.extend(block)
                    if len(data) > 64 * 1024 * 1024: raise ValueError('Native owner capture exceeds bound')
        if tool.wait(timeout=5) != 0: raise RuntimeError('Native owner tool failed')
        text = data.decode('utf-8')
        if owner_diagnostic(text): raise RuntimeError('Native owner tool emitted a diagnostic')
        return owner_summary(text)
    finally:
        path.write_bytes(data)
        if tool.poll() is None:
            tool.terminate()
            try: tool.wait(timeout=5)
            except subprocess.TimeoutExpired: tool.kill(); tool.wait(timeout=5)
        tool.stdout.close()


def run(binary, output, mode, trace, waves=24, tiering_off=False, owners=False):
    if sys.platform != 'darwin':
        raise ValueError('This controller requires macOS vmmap')
    output.mkdir(parents=True, exist_ok=False)
    settings = dict(os.environ, TRYAGI_RETENTION_MODE=mode, TRYAGI_RETENTION_TRACE=str(trace), TRYAGI_RETENTION_PROFILE='1', TRYAGI_RETENTION_WAVES=str(waves))
    if tiering_off: settings['DOTNET_TieredCompilation'] = '0'
    if owners: settings.update(MallocStackLogging='1', TRYAGI_RETENTION_QUIESCENCE='1')
    expected = [('ready', -5)] + [('disposed', w) for w in ([2, 16, 32, 63] if waves == 64 else [2, 16])]
    if owners: expected += [('quiescent_10', waves - 1), ('quiescent_30', waves - 1)]
    samples = []
    started = time.monotonic()
    command = (['dotnet', str(binary.resolve())] if binary.suffix == '.dll' else [str(binary.resolve())]) + ['--suite', 'retention']
    stderr_log = (output / 'child-stderr.txt').open('wb')
    child = subprocess.Popen(command,
                             stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=stderr_log, env=settings)
    try:
        with selectors.DefaultSelector() as selector, (output / 'retention.csv').open('wb') as log:
            selector.register(child.stdout, selectors.EVENT_READ)
            pending = b''
            total = 0
            while selector.get_map():
                if time.monotonic() - started > (600 if waves == 64 else 210):
                    raise TimeoutError('Owned retention controller exceeded its deadline')
                if (output / 'child-stderr.txt').stat().st_size > 1024 * 1024: raise ValueError('Child stderr exceeds bound')
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
                        phase, point = expected[0]
                        wave = barrier(line, point, phase)
                        expected.pop(0)
                        if wave != -5:
                            capture = subprocess.run(['/usr/bin/vmmap', '-summary', str(child.pid)],
                                                     capture_output=True, timeout=15)
                            suffix = f'{wave}-{phase}' if owners else str(wave)
                            (output / f'vmmap-wave-{suffix}.txt').write_bytes(capture.stdout + capture.stderr)
                            if capture.returncode or re.search(rb'\b(?:warning|error|cannot|unable)\b', capture.stdout + capture.stderr, re.I):
                                raise RuntimeError('Native memory capture failed')
                            sample = dict(wave=wave, phase=phase, alive_disposed_peers=0,
                                                malloc=native_summary(capture.stdout.decode('utf-8')),
                                                elapsed_seconds=time.monotonic() - started)
                            if owners:
                                sample['native_owners'] = capture_owners(child.pid, output / f'malloc-{suffix}.txt')
                            samples.append(sample)
                        child.stdin.write(f'continue:{wave}\n'.encode())
                        child.stdin.flush()
            if child.wait(timeout=10) != 0 or expected:
                raise RuntimeError('Retention child failed or omitted a profiling barrier')
        if re.search(r'\b(?:warning|error|cannot|unable)\b', (output / 'child-stderr.txt').read_text(), re.I):
            raise RuntimeError('Retention child emitted a diagnostic')
        result = dict(mode=mode, trace=trace, waves=waves, tiering_off=tiering_off, owners=owners, samples=samples, child_exit=0,
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
        stderr_log.close()
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
    parser.add_argument('--owners', action='store_true', help='Instrumented native-stack cohort with 10/30s quiescence; not ordinary budget data')
    args = parser.parse_args()
    run(args.binary, args.output, args.mode, args.trace, args.waves, args.tiering_off, args.owners)
