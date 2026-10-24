#!/usr/bin/env python3
"""Summarize instrumented native stacks separately from resident-memory observations."""
import argparse
import importlib.util
import json
from pathlib import Path
import statistics

spec = importlib.util.spec_from_file_location('retention', Path(__file__).with_name('summarize-native-retention.py'))
retention = importlib.util.module_from_spec(spec); spec.loader.exec_module(retention)
KINDS = {'vm_request', 'malloc_request', 'unknown_request'}
OWNERS = {'dotnet_jit', 'dotnet_runtime', 'crypto_sdk', 'foundation_sdk', 'swift_runtime', 'socket_sdk', 'unknown_or_other'}
PHASES = [('disposed', w) for w in (2, 16, 32, 63)] + [('quiescent_10', 63), ('quiescent_30', 63)]


def analyze(data, rows, mode):
    if data['mode'] != mode or data['trace'] != 0 or data['waves'] != 64 or data['child_exit'] != 0 or data.get('owners') is not True or data['tiering_off'] is not True:
        raise ValueError('Unexpected instrumented native cohort')
    expected = [('disposed', w) for w in range(64)] + PHASES[-2:]
    if [(r['phase'], int(r['wave'])) for r in rows] != expected or any(r['mode'] != mode or r['trace'] != 'False' or int(r['alive_disposed_peers']) != 0 for r in rows):
        raise ValueError('Incomplete, mismatched or rooted instrumented cohort')
    if [(s['phase'], s['wave']) for s in data['samples']] != PHASES: raise ValueError('Native phase coverage mismatch')
    phases = []
    for sample in data['samples']:
        row = next(r for r in rows if r['phase'] == sample['phase'] and int(r['wave']) == sample['wave'])
        if sample['alive_disposed_peers'] != 0: raise ValueError('Rooted peer at native barrier')
        heap = sample['malloc']
        if heap['total_source'] not in ('explicit_total', 'single_zone'): raise ValueError('Missing native heap total')
        owners = sample['native_owners']; groups = owners['groups']
        if not 0 < owners['records'] <= 100000: raise ValueError('Native record count invalid')
        sanitized = {}
        for key, group in groups.items():
            kind, owner = key.split('/')
            if kind not in KINDS or owner not in OWNERS or set(group) != {'calls', 'requested_bytes'} or any(type(v) is not int or v < 0 for v in group.values()):
                raise ValueError('Unknown native owner or malformed numeric count')
            sanitized[key] = dict(group)
        if not sanitized: raise ValueError('Missing native owner groups')
        values = {key: int(row[key]) for key in ('rss', 'managed_committed', 'threadpool_threads', 'threadpool_pending', 'threadpool_completed')}
        if any(value < 0 for value in values.values()): raise ValueError('Negative runtime counter')
        phases.append(dict(phase=sample['phase'], wave=sample['wave'], **values,
            malloc_resident=retention.size(heap['resident']), malloc_allocated=retention.size(heap['allocated']), native_requests=sanitized))
    return phases


def summarize(root):
    results = {}; identities = set()
    for mode in ('empty_control', 'peer_open'):
        paths = sorted((Path(root) / mode).glob('*/profile.json'))
        if len(paths) != 3: raise ValueError('Exactly three fresh processes per workload required')
        repetitions = []
        for path in paths:
            if path.stat().st_size > 256 * 1024: raise ValueError('Native profile exceeds bound')
            data = json.loads(path.read_text())
            cohort, source, rows = retention.core.read_csv(path.parent / 'retention.csv', 'mode', allow_tiering_control=True)
            if not cohort.endswith('|tiered_compilation=0'): raise ValueError('Tiering-off cohort required')
            identities.add((cohort, source))
            repetitions.append(analyze(data, rows, mode))
        names = ('rss', 'managed_committed', 'malloc_resident', 'malloc_allocated', 'threadpool_threads', 'threadpool_pending', 'threadpool_completed')
        def delta(start, end):
            return {key: statistics.median(r[end][key] - r[start][key] for r in repetitions) for key in names}
        results[mode] = dict(repetitions=repetitions, median_growth_wave2_to63=delta(0, 3), median_quiet_change=delta(3, 5))
    if len(identities) != 1: raise ValueError('Mixed instrumented source/runtime/hardware cohort')
    cohort, source = next(iter(identities))
    return dict(source=source, cohort=cohort, workloads=results, leak_freedom_verdict=False,
        scope='MallocStackLogging instrumented; native request sizes are not resident bytes; VM/malloc overlap; nearest native frame is descriptive, not causal attribution; quiet waits exclude profiling time')


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__); p.add_argument('directory'); p.add_argument('--output', required=True)
    args = p.parse_args(); Path(args.output).write_text(json.dumps(summarize(args.directory), indent=2, sort_keys=True) + '\n')
