#!/usr/bin/env python3
"""Exact guard contracts and reviewed per-sample authentication rejection budgets."""
import argparse
import importlib.util
import itertools
import json
import math
from pathlib import Path

spec = importlib.util.spec_from_file_location('base_budget', Path(__file__).with_name('check-performance-budgets.py'))
base = importlib.util.module_from_spec(spec)
spec.loader.exec_module(base)
MODES = ('bad-tag', 'wrong-key', 'replay')
PACKETS = {'rtp-audio': 72, 'rtp-video': 1212, 'rtcp': 28}
GROUPS = tuple(itertools.product(base.PROFILES, PACKETS, MODES))


def read_process(path):
    cohort, source, rows = base.read_csv(path, 'profile')
    seen, groups = set(), {}
    for row in rows:
        key = row['profile'], row['packet'], row['mode']
        sample = int(row['sample'])
        if key not in GROUPS or sample not in range(7) or (key, sample) in seen or int(row['operations']) != 1000 or int(row['packet_bytes']) != PACKETS[row['packet']]:
            raise base.BudgetViolation('Rejection workload coverage mismatch')
        seen.add((key, sample))
        raw = base.total_bytes(row)
        for name in ('process_total_bytes', 'gen0', 'gen1', 'gen2'):
            if not row[name].isdigit():
                raise base.BudgetViolation('Invalid rejection counter')
        for name in ('ns_per_op', 'cpu_ms'):
            base.number(row[name])
        if (key[0] == base.PROFILES[0] or key[2] == 'replay') and raw != 0:
            raise base.BudgetViolation('Exact rejection guard allocation exceeded')
        groups.setdefault('/'.join(key), []).append(raw / 1000)
    if seen != set(itertools.product(GROUPS, range(7))):
        raise base.BudgetViolation('Missing rejection samples')
    return cohort, source, groups


def processes(directory):
    records = [read_process(Path(directory) / f'rejection-{i}.csv') for i in (1, 2, 3)]
    if len({(r[0], r[1]) for r in records}) != 1:
        raise base.BudgetViolation('Mixed rejection process identities')
    return records[0][0], records[0][1], [r[2] for r in records]


def calibrate(directories, source):
    cohorts = {}
    for directory in directories:
        cohort, actual, records = processes(directory)
        if actual != source or cohort in cohorts:
            raise base.BudgetViolation('Calibration source mismatch or duplicate cohort')
        # All GCM failure cases use the same throwing framework API. A JIT
        # call path's stack cost can move between scenarios across processes;
        # train a shared failure-path cap, never widen exact-zero guards.
        failure_keys = [key for key in records[0] if not key.startswith(base.PROFILES[0] + '/') and not key.endswith('/replay')]
        maximum = max(value for record in records[:2] for key in failure_keys for value in record[key])
        proposed = maximum * 1.25 + 64
        if not math.isfinite(proposed):
            raise base.BudgetViolation('Unrepresentable rejection ceiling')
        failure_limit = 0 if maximum == 0 else math.ceil(proposed / 64) * 64
        limits = {key: failure_limit if key in failure_keys else 0 for key in records[0]}
        for key, limit in limits.items():
            if max(records[2][key]) > limit:
                raise base.BudgetViolation('Independent rejection holdout exceeds proposed budget')
        cohorts[cohort] = dict(ceilings_bytes_per_op=limits,
                              training_maxima=[{k: max(v) for k, v in r.items()} for r in records[:2]],
                              holdout_maxima={k: max(v) for k, v in records[2].items()})
    return dict(schema_version=1, source_commit=source,
                policy='maximum_sample; shared_GCM_framework_failure_path_per_compiler_cohort; exact_zero_AES_CM_and_replay; train_1_2_holdout_3; failure_max_x1.25_plus64_roundup64; no_timing_gate',
                cohorts=cohorts)


def check(directory, budgets=None):
    cohort, source, records = processes(directory)
    if budgets is not None:
        if budgets.get('schema_version') != 1 or cohort not in budgets.get('cohorts', {}):
            raise base.BudgetViolation('Unreviewed rejection runtime/platform/compiler cohort')
        limits = budgets['cohorts'][cohort]['ceilings_bytes_per_op']
        for record in records:
            if set(record) != set(limits):
                raise base.BudgetViolation('Rejection budget coverage mismatch')
            for key, values in record.items():
                if max(values) > base.number(limits[key]):
                    raise base.BudgetViolation(f'Rejection allocation budget exceeded: {key}')
    return dict(verdict='capture_valid' if budgets is None else 'pass', cohort=cohort, source=source,
                samples=567, processes=3, limits_auto_updated=False,
                maxima_bytes_per_op={key: max(max(r[key]) for r in records) for key in records[0]})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', nargs='?')
    parser.add_argument('--budgets', default='specs/rejection-performance-budgets.json')
    parser.add_argument('--capture-only', action='store_true')
    parser.add_argument('--calibrate', nargs='+')
    parser.add_argument('--source')
    parser.add_argument('--output')
    args = parser.parse_args()
    try:
        if args.calibrate:
            if not args.source or not base.re.fullmatch('[0-9a-f]{40}', args.source):
                raise base.BudgetViolation('Calibration requires exact source')
            result = calibrate(args.calibrate, args.source)
        else:
            result = check(args.directory, None if args.capture_only else json.loads(Path(args.budgets).read_text()))
    except (ValueError, KeyError, OSError, TypeError) as error:
        result = dict(verdict='fail', reason=str(error), limits_auto_updated=False)
    text = json.dumps(result, indent=2, sort_keys=True) + '\n'
    if args.output:
        Path(args.output).write_text(text)
    print(text, end='')
    return int(result['verdict'] == 'fail') if 'verdict' in result else 0


if __name__ == '__main__':
    raise SystemExit(main())
