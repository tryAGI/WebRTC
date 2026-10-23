#!/usr/bin/env python3
"""Capture only an owned child's post-disposal barriers; never suspend/resume it."""
import argparse
import json
import os
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


def run(binary, output, mode, trace):
    if sys.platform != 'darwin':
        raise ValueError('This controller requires macOS vmmap')
    output.mkdir(parents=True, exist_ok=False)
    settings = dict(os.environ, TRYAGI_RETENTION_MODE=mode, TRYAGI_RETENTION_TRACE=str(trace), TRYAGI_RETENTION_PROFILE='1')
    expected = [-5, 2, 16]
    samples = []
    started = time.monotonic()
    child = subprocess.Popen(['dotnet', str(binary.resolve()), '--suite', 'retention'],
                             stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, env=settings)
    try:
        with selectors.DefaultSelector() as selector, (output / 'retention.csv').open('wb') as log:
            selector.register(child.stdout, selectors.EVENT_READ)
            pending = b''
            total = 0
            while selector.get_map():
                if time.monotonic() - started > 210:
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
                                                elapsed_seconds=time.monotonic() - started))
                        child.stdin.write(f'continue:{wave}\n'.encode())
                        child.stdin.flush()
            if child.wait(timeout=10) != 0 or expected:
                raise RuntimeError('Retention child failed or omitted a profiling barrier')
        result = dict(mode=mode, trace=trace, samples=samples, child_exit=0,
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
    parser.add_argument('--mode', choices=('empty_control', 'identity_only', 'peer_audio', 'peer_open'), default='peer_open')
    parser.add_argument('--trace', type=int, choices=(0, 1), default=0)
    args = parser.parse_args()
    run(args.binary, args.output, args.mode, args.trace)
