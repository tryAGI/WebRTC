#!/usr/bin/env python3
"""Compare worker retirement and stack-logging counterfactuals on one recorded cohort."""
import argparse
import importlib.util
import json
from pathlib import Path
import statistics

spec = importlib.util.spec_from_file_location('native', Path(__file__).with_name('summarize-native-retention.py'))
native = importlib.util.module_from_spec(spec); spec.loader.exec_module(native)
PHASES = [('disposed', w) for w in (2, 16, 23)] + [('quiescent_10', 23), ('quiescent_30', 23)]
METRICS = ('rss', 'managed_committed', 'threadpool_threads', 'threadpool_pending')


def phases(data, rows, minimum, traced, default_minimum=3):
    if data['mode'] != 'peer_open' or data['trace'] != 0 or data['waves'] != 24 or data['child_exit'] != 0 or data.get('owners') is not traced or data.get('quiescence') is not True or data.get('worker_minimum') != minimum or data['tiering_off'] is not True:
        raise ValueError('Unexpected worker counterfactual cohort')
    expected = [('disposed', w) for w in range(24)] + PHASES[-2:]
    if [(r['phase'], int(r['wave'])) for r in rows] != expected or any(r['mode'] != 'peer_open' or r['trace'] != 'False' or int(r['alive_disposed_peers']) != 0 for r in rows): raise ValueError('Incomplete or rooted worker cohort')
    if [(s['phase'], s['wave']) for s in data['samples']] != PHASES: raise ValueError('Worker capture phase mismatch')
    result = []
    for sample in data['samples']:
        row = next(r for r in rows if r['phase'] == sample['phase'] and int(r['wave']) == sample['wave'])
        if int(row['threadpool_minimum']) != (24 if minimum else default_minimum): raise ValueError('Observed worker minimum differs from control or recorded host')
        values = {name: int(row[name]) for name in METRICS}
        if any(v < 0 for v in values.values()) or values['threadpool_pending'] != 0 or sample['alive_disposed_peers'] != 0: raise ValueError('Negative, pending or rooted worker snapshot')
        heap = sample['malloc']
        if heap['total_source'] not in ('single_zone', 'explicit_total'): raise ValueError('Missing allocator total')
        entry = dict(phase=sample['phase'], wave=sample['wave'], **values,
            malloc_dirty=native.size(heap['dirty']), malloc_resident=native.size(heap['resident']), malloc_allocated=native.size(heap['allocated']), malloc_fragmentation=native.size(heap['fragmentation']))
        if traced:
            owners = sample['native_owners']['groups']
            for key in ('unknown_request/dotnet_runtime', 'malloc_request/dotnet_runtime', 'malloc_request/crypto_sdk'):
                value = owners.get(key, dict(calls=0, requested_bytes=0))
                if set(value) != {'calls', 'requested_bytes'} or any(type(v) is not int or v < 0 for v in value.values()): raise ValueError('Malformed native owner group')
                entry[key] = dict(value)
        elif 'native_owners' in sample: raise ValueError('Uninstrumented sample contains traced owner data')
        result.append(entry)
    return result


def summarize(root):
    controls = {}; identity = set()
    for traced in (False, True):
        for minimum in (0, 24):
            name = ('traced' if traced else 'plain') + '-' + str(minimum)
            paths = sorted((Path(root) / name).glob('*/profile.json'))
            if len(paths) != 3: raise ValueError('Three fresh processes required for every counterfactual')
            reps = []
            for path in paths:
                if path.stat().st_size > 256 * 1024: raise ValueError('Worker profile exceeds bound')
                cohort, source, rows = native.core.read_csv(path.parent / 'retention.csv', 'mode', allow_tiering_control=True)
                parts = cohort.split('|')
                cpu = int(next(part.split('=', 1)[1] for part in parts if part.startswith('cpu=')))
                if not 0 < cpu < 24 or 'tiered_compilation=0' not in parts or ('native_stack_logging=1' in parts) != traced or ('worker_minimum=24' in parts) != bool(minimum): raise ValueError('Worker runtime/cohort identity mismatch')
                identity.add(('|'.join(p for p in parts if not p.startswith(('worker_minimum=', 'native_stack_logging='))), source))
                reps.append(phases(json.loads(path.read_text()), rows, minimum, traced, cpu))
            names = METRICS + ('malloc_dirty', 'malloc_resident', 'malloc_allocated', 'malloc_fragmentation')
            controls[name] = dict(repetitions=reps,
                median_wave2_to23={k: statistics.median(rep[2][k] - rep[0][k] for rep in reps) for k in names},
                median_final_quiet_change={k: statistics.median(rep[4][k] - rep[2][k] for rep in reps) for k in names},
                median_quiet30={k: statistics.median(rep[4][k] for rep in reps) for k in names})
    if len(identity) != 1: raise ValueError('Mixed worker source/runtime/hardware cohort')
    cohort, source = next(iter(identity))
    names = METRICS + ('malloc_dirty', 'malloc_resident', 'malloc_allocated', 'malloc_fragmentation')
    differences = {kind: {k: controls[kind + '-24']['median_quiet30'][k] - controls[kind + '-0']['median_quiet30'][k] for k in names} for kind in ('plain', 'traced')}
    return dict(source=source, cohort=cohort, controls=controls, minimum24_minus_default_quiet30=differences,
        leak_freedom_verdict=False, scope='owned diagnostic child; minimum-worker and stack-logging interventions are separate; nearest native frame is not allocator identity; malloc values inherit vmmap rounding')


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__); p.add_argument('directory'); p.add_argument('--output', required=True)
    args = p.parse_args(); Path(args.output).write_text(json.dumps(summarize(args.directory), indent=2, sort_keys=True) + '\n')
