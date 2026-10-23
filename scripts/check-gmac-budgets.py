#!/usr/bin/env python3
"""Measure empty-ciphertext GMAC rejection; optional exact-zero acceptance."""
import argparse
import importlib.util
import itertools
import json
from pathlib import Path

spec = importlib.util.spec_from_file_location('rejection', Path(__file__).with_name('check-rejection-budgets.py'))
rejection = importlib.util.module_from_spec(spec); spec.loader.exec_module(rejection)
base = rejection.base
PACKETS = {'rtp-empty': 12, 'rtcp-empty': 8, 'rtcp-auth-only': 28}
GROUPS = tuple(itertools.product(base.PROFILES[1:], PACKETS, rejection.MODES))


def check(directory, zero=False):
    identities, maxima = set(), {}
    for repetition in (1, 2, 3):
        cohort, source, rows = base.read_csv(Path(directory) / f'gmac-{repetition}.csv', 'profile')
        identities.add((cohort, source)); seen = set()
        for row in rows:
            key = row['profile'], row['packet'], row['mode']
            sample = int(row['sample'])
            if key not in GROUPS or sample not in range(7) or (key, sample) in seen or int(row['operations']) != 1000 or int(row['packet_bytes']) != PACKETS[row['packet']]:
                raise ValueError('GMAC workload coverage mismatch')
            seen.add((key, sample)); raw = base.total_bytes(row)
            for name in ('process_total_bytes', 'gen0', 'gen1', 'gen2'):
                if not row[name].isdigit(): raise ValueError('Invalid GMAC counter')
            for name in ('ns_per_op', 'cpu_ms'): base.number(row[name])
            if (zero or key[2] == 'replay') and raw != 0: raise ValueError('Exact GMAC allocation contract exceeded')
            name = '/'.join(key); maxima[name] = max(maxima.get(name, 0), raw / 1000)
        if seen != set(itertools.product(GROUPS, range(7))): raise ValueError('Missing GMAC samples')
    if len(identities) != 1: raise ValueError('Mixed GMAC process identities')
    cohort, source = next(iter(identities))
    return dict(verdict='pass' if zero else 'capture_valid', cohort=cohort, source=source,
                processes=3, samples=378, maxima_bytes_per_op=maxima, exact_zero=zero, limits_auto_updated=False)


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__); p.add_argument('directory'); p.add_argument('--zero', action='store_true'); p.add_argument('--output', required=True)
    args = p.parse_args(); result = check(args.directory, args.zero)
    Path(args.output).write_text(json.dumps(result, indent=2, sort_keys=True) + '\n')
