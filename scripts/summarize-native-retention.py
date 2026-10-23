#!/usr/bin/env python3
"""Summarize repeated matched-phase malloc controls, without a leak-freedom verdict."""
import argparse
import csv
import importlib.util
import json
from pathlib import Path
import re
import statistics

spec = importlib.util.spec_from_file_location('core', Path(__file__).with_name('check-performance-budgets.py'))
core = importlib.util.module_from_spec(spec); spec.loader.exec_module(core)


def size(value):
    found = re.fullmatch(r'([0-9]+(?:\.[0-9]+)?)([BKMGT]?)', value)
    if not found: raise ValueError('Unknown native size notation')
    return float(found[1]) * (1024 ** ('BKMGT'.index(found[2]) if found[2] else 0))


def summarize(root, mode):
    paths = sorted(Path(root).glob('*/profile.json'))
    if len(paths) != 3: raise ValueError('Exactly three fresh profiling processes required')
    identities, repetitions = set(), []
    for path in paths:
        if path.stat().st_size > 65536: raise ValueError('Profile summary exceeds bound')
        data = json.loads(path.read_text())
        if data['mode'] != mode or data['trace'] != 0 or data['waves'] != 64 or data['child_exit'] != 0:
            raise ValueError('Unexpected memory cohort')
        cohort, source, rows = core.read_csv(path.parent / 'retention.csv', 'mode')
        identities.add((cohort, source))
        if len(rows) != 64 or {int(r['wave']) for r in rows} != set(range(64)) or any(r['mode'] != mode or r['trace'] != 'False' or r['phase'] != 'disposed' or int(r['alive_disposed_peers']) != 0 for r in rows):
            raise ValueError('Incomplete or rooted disposed-peer cohort')
        samples = data['samples']
        if [s['wave'] for s in samples] != [2, 16, 32, 63] or any(s['phase'] != 'disposed' or s['alive_disposed_peers'] != 0 for s in samples):
            raise ValueError('Mismatched native capture phases')
        phases = []
        for sample in samples:
            malloc = sample['malloc']
            if malloc['total_source'] not in ('explicit_total', 'single_zone'): raise ValueError('No attributable malloc total')
            row = next(r for r in rows if int(r['wave']) == sample['wave'])
            phases.append(dict(wave=sample['wave'], malloc_resident=size(malloc['resident']),
                               malloc_allocated=size(malloc['allocated']), malloc_fragmentation=size(malloc['fragmentation']),
                               managed_committed=int(row['managed_committed']), rss=int(row['rss'])))
        names = ('malloc_resident', 'malloc_allocated', 'malloc_fragmentation', 'managed_committed', 'rss')
        repetitions.append(dict(phases=phases, delta={k: phases[-1][k] - phases[0][k] for k in names},
                                tail_bytes_per_wave={k: (phases[-1][k] - phases[-2][k]) / 31 for k in names}))
    if len(identities) != 1: raise ValueError('Mixed memory source/runtime/hardware metadata')
    cohort, source = next(iter(identities))
    def ranges(field):
        return {k: dict(min=min(r[field][k] for r in repetitions), median=statistics.median(r[field][k] for r in repetitions),
                        max=max(r[field][k] for r in repetitions)) for k in repetitions[0][field]}
    return dict(mode=mode, source=source, cohort=cohort, processes=3, waves=64,
                repetitions=repetitions, delta_bytes=ranges('delta'), tail_bytes_per_wave=ranges('tail_bytes_per_wave'),
                leak_freedom_verdict=False, precision='malloc values inherit vmmap rounding; RSS and GC committed are separate measurements')


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__); p.add_argument('directory'); p.add_argument('--mode', required=True); p.add_argument('--output', required=True)
    args = p.parse_args(); result = summarize(args.directory, args.mode)
    Path(args.output).write_text(json.dumps(result, indent=2, sort_keys=True) + '\n')
