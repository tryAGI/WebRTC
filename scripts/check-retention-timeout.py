#!/usr/bin/env python3
"""Prove a missing controller acknowledgement exits the owned child within 30 seconds."""
import argparse
import os
from pathlib import Path
import subprocess
import time


def check(binary):
    command = (['dotnet', str(binary.resolve())] if binary.suffix == '.dll' else [str(binary.resolve())]) + ['--suite', 'retention']
    settings = dict(os.environ, TRYAGI_RETENTION_PROFILE='1', TRYAGI_RETENTION_MODE='empty_control', TRYAGI_RETENTION_WAVES='24')
    child = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, env=settings)
    start = time.monotonic()
    try:
        # Keep stdin OPEN without acknowledging. communicate() would close it and
        # test EOF instead of the cancellation deadline.
        code = child.wait(timeout=30)
        output = child.stdout.read(65537)
        elapsed = time.monotonic() - start
        if code != 1 or b'Retention deadline expired' not in output or len(output) > 65536 or b'# profile_barrier=ready;wave=-5;alive_disposed_peers=0' not in output or elapsed < 18:
            raise ValueError('Missing-ack test did not reach the expected bounded cancellation')
        print(f'Missing-ack cancellation verified in {elapsed:.2f}s; expected nonzero child exit')
    finally:
        if child.poll() is None:
            child.terminate()
            try: child.wait(timeout=5)
            except subprocess.TimeoutExpired:
                child.kill(); child.wait(timeout=5)
        child.stdin.close(); child.stdout.close()


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__); p.add_argument('binary', type=Path)
    check(p.parse_args().binary)
