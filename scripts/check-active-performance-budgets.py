#!/usr/bin/env python3
"""Reviewed allocation ceilings for existing active pipeline workloads; no time gates."""
import argparse
import importlib.util
import itertools
import json
import math
from pathlib import Path
import statistics
import sys

_spec = importlib.util.spec_from_file_location('allocation_core', Path(__file__).with_name('check-performance-budgets.py'))
core = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(core)
SPECS = {
    'media': ('profile', ('profile', 'payload_bytes', 'trace', 'caller_token'), 'process_managed_bytes_per_packet', 'packets', 512,
              tuple(itertools.product(core.PROFILES, ('60', '1000'), ('False', 'True'), ('False', 'True')))),
    'audio': ('profile', ('profile', 'trace', 'payload_bytes'), 'process_bytes_per_packet', 'delivered_packets', 1024,
              tuple(itertools.product(core.PROFILES, ('False', 'True'), ('60', '1000')))),
    'channels': ('payload_bytes', ('payload_bytes',), 'process_managed_bytes_per_message', 'messages', 256, (('60',), ('4096',))),
    'video': ('codec', ('codec',), 'managed_bytes_per_frame', 'frames', 2000, (('H264',), ('Vp8',))),
}


def measurements(directory):
    identities = set()
    medians = {}
    for suite, (header, fields, metric, count_field, count, expected) in SPECS.items():
        cohort, source, rows = core.read_csv(Path(directory) / (suite + '.csv'), header)
        identities.add((cohort, source))
        groups, seen = {}, set()
        for row in rows:
            key = tuple(row[field] for field in fields)
            sample = int(row['sample'])
            if key not in expected or sample not in range(5) or (key, sample) in seen or int(row[count_field]) != count:
                raise core.BudgetViolation('Active workload coverage mismatch: ' + suite)
            seen.add((key, sample))
            groups.setdefault(key, []).append(core.number(row[metric]))
        if set(groups) != set(expected) or any(len(values) != 5 for values in groups.values()):
            raise core.BudgetViolation('Missing active workload samples: ' + suite)
        medians.update({suite + '/' + '/'.join(key): statistics.median(values) for key, values in groups.items()})
    if len(identities) != 1:
        raise core.BudgetViolation('Mixed active workload source/runtime identity')
    return *next(iter(identities)), medians


def calibrate(directories, sources, runtime_tree):
    if len(set(sources)) != 2 or not all(core.re.fullmatch('[0-9a-f]{40}', source) for source in [*sources, runtime_tree]):
        raise core.BudgetViolation('Two exact calibration sources and equivalent runtime tree required')
    grouped = {}
    for directory in directories:
        cohort, source, medians = measurements(directory)
        if source not in sources or source in grouped.setdefault(cohort, {}):
            raise core.BudgetViolation('Unexpected or duplicate active calibration source')
        grouped[cohort][source] = medians
    cohorts = {}
    for cohort, process in grouped.items():
        if set(process) != set(sources):
            raise core.BudgetViolation('Two independent active training processes required')
        limits = {}
        for key in process[sources[0]]:
            candidate = max(values[key] for values in process.values()) * 1.5 + 64
            if not math.isfinite(candidate):
                raise core.BudgetViolation('Unrepresentable active ceiling')
            limits[key] = math.ceil(candidate / 64) * 64
        cohorts[cohort] = {'ceilings_bytes_per_operation': limits, 'training_processes': process}
    return {'schema_version': 1, 'calibration_sources': sources, 'equivalent_runtime_tree': runtime_tree,
            'policy': 'five_sample_medians; two_runtime_equivalent_training_processes; max_x1.5_plus64_roundup64; fresh_CI_holdout_required; no_timing_gate',
            'scopes': {'media': 'process/packet', 'audio': 'process/delivered_packet', 'channels': 'process/message', 'video': 'current_thread/owned_frame'},
            'cohorts': cohorts}


def calibrate_aot_repetitions(directories, source, runtime_tree):
    if not all(core.re.fullmatch('[0-9a-f]{40}', value) for value in (source, runtime_tree)):
        raise core.BudgetViolation('Exact source and runtime tree required')
    cohorts = {}
    for directory in directories:
        records = [measurements(Path(directory) / f'active-{i}') for i in (1, 2, 3)]
        if len({(r[0], r[1]) for r in records}) != 1:
            raise core.BudgetViolation('Mixed repeated active identities')
        cohort, actual, _ = records[0]
        if actual != source or not cohort.endswith('dynamic_code=False') or cohort in cohorts:
            raise core.BudgetViolation('Expected unique NativeAOT calibration cohort')
        process = [r[2] for r in records]
        limits = {}
        for key in process[0]:
            proposed = max(process[0][key], process[1][key]) * 1.5 + 64
            if not math.isfinite(proposed): raise core.BudgetViolation('Unrepresentable active ceiling')
            limits[key] = math.ceil(proposed / 64) * 64
            if process[2][key] > limits[key]:
                raise core.BudgetViolation('Independent active holdout exceeds proposed budget')
        cohorts[cohort] = dict(ceilings_bytes_per_operation=limits, training_processes=process[:2], holdout_process=process[2])
    return dict(schema_version=1, calibration_sources=[source], equivalent_runtime_tree=runtime_tree,
                policy='five_sample_medians; three_fresh_AOT_processes; train_1_2_holdout_3; max_x1.5_plus64_roundup64; no_timing_gate',
                scopes={'media':'process/packet','audio':'process/delivered_packet','channels':'process/message','video':'current_thread/owned_frame'},
                cohorts=cohorts)


def check(directory, budgets):
    cohort, source, medians = measurements(directory)
    if budgets.get('schema_version') != 1 or cohort not in budgets['cohorts']:
        raise core.BudgetViolation('Unreviewed active runtime/platform cohort')
    limits = budgets['cohorts'][cohort]['ceilings_bytes_per_operation']
    if set(limits) != set(medians):
        raise core.BudgetViolation('Active budget workload coverage mismatch')
    for key, value in medians.items():
        if value > core.number(limits[key]):
            raise core.BudgetViolation(f'Active allocation budget exceeded: {key}: {value} > {limits[key]}')
    return {'verdict': 'pass', 'source': source, 'cohort': cohort, 'workloads': len(medians), 'limits_auto_updated': False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', nargs='?')
    parser.add_argument('--budgets', default='specs/active-performance-budgets.json')
    parser.add_argument('--calibrate', nargs='+')
    parser.add_argument('--calibrate-aot-repetitions', nargs='+')
    parser.add_argument('--capture-only', action='store_true')
    parser.add_argument('--source')
    parser.add_argument('--sources', nargs=2)
    parser.add_argument('--runtime-tree')
    parser.add_argument('--output')
    args = parser.parse_args()
    try:
        if args.calibrate_aot_repetitions:
            result = calibrate_aot_repetitions(args.calibrate_aot_repetitions, args.source or '', args.runtime_tree or '')
        elif args.capture_only and args.directory:
            cohort, source, medians = measurements(args.directory)
            result = dict(verdict='capture_valid', cohort=cohort, source=source, workloads=len(medians), limits_auto_updated=False, medians=medians)
        elif args.calibrate:
            result = calibrate(args.calibrate, args.sources or [], args.runtime_tree or '')
        elif args.directory:
            result = check(args.directory, json.loads(Path(args.budgets).read_text()))
        else:
            raise core.BudgetViolation('Result directory required')
    except (ValueError, OSError, KeyError, TypeError) as error:
        result = {'verdict': 'fail', 'reason': str(error), 'limits_auto_updated': False}
        status = 1
    else:
        status = 0
    text = json.dumps(result, indent=2, sort_keys=True) + '\n'
    if args.output:
        Path(args.output).write_text(text)
    print(text, end='')
    return status


if __name__ == '__main__':
    sys.exit(main())
