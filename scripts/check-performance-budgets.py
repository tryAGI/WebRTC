#!/usr/bin/env python3
"""Fail closed on exact-zero contracts and reviewed matched-cohort idle budgets."""
import argparse
import csv
import itertools
import json
import math
from pathlib import Path
import re
import statistics
import sys

PROFILES = ('Aes128CmHmacSha1_80', 'AeadAes128Gcm', 'AeadAes256Gcm')
OPERATIONS = ('rtp-mid', 'rtp-truncated', 'rtcp-framing', 'stun-fingerprint')
IDLE_GROUPS = tuple(itertools.product(('off', 'associated', 'open'), ('False', 'True'), (1, 3)))


class BudgetViolation(ValueError):
    pass


def read_csv(path, header):
    path = Path(path)
    if path.stat().st_size > 4 * 1024 * 1024:
        raise BudgetViolation('Result file exceeds four MiB bound')
    lines = path.read_text(encoding='utf-8-sig').splitlines()
    if len(lines) < 5 or not lines[0].startswith('.NET '):
        raise BudgetViolation('Missing runtime metadata')
    runtime, operating_system, architecture = lines[0].split('; ')
    os_family = 'windows' if 'Windows' in operating_system else 'macos' if 'macOS' in operating_system else 'linux' if any(name in operating_system for name in ('Linux', 'Ubuntu', 'Debian')) else None
    if os_family is None:
        raise BudgetViolation('Unreviewed operating-system family')
    settings = dict(item.split('=', 1) for item in lines[1].split('; '))
    library = dict(item.split('=', 1) for item in lines[2].split('; '))
    source = library.get('library_source', '')
    if not re.fullmatch('[0-9a-f]{40}', source) or lines[3] != 'harness_source=' + source:
        raise BudgetViolation('Library and harness source identity must match')
    cohort = '|'.join((runtime, os_family, architecture, 'cpu=' + settings['cpu_count'],
                       'server_gc=' + settings['server_gc'], 'dynamic_code=' + settings['dynamic_code']))
    if not lines[4].startswith(header + ','):
        raise BudgetViolation('Unexpected result schema')
    rows = list(csv.DictReader(line for line in lines[4:] if not line.startswith('#')))
    if len(rows) > 10000 or any(None in row for row in rows):
        raise BudgetViolation('Result row bound or framing invalid')
    return cohort, source, rows


def number(value):
    result = float(value)
    if not math.isfinite(result) or result < 0:
        raise BudgetViolation('Nonfinite or negative metric')
    return result


def total_bytes(row):
    text = row.get('managed_total_bytes', '')
    if not re.fullmatch(r'0|[1-9][0-9]*', text):
        raise BudgetViolation('Missing exact total allocation count')
    return int(text)


def zero_rows(rows, crypto):
    expected = set(itertools.product(PROFILES, (60, 1200), range(7))) if crypto else set(itertools.product(OPERATIONS, range(7)))
    seen = set()
    for row in rows:
        key = (row['profile'], int(row['payload_bytes']), int(row['sample'])) if crypto else (row['operation'], int(row['sample']))
        if key not in expected or key in seen or int(row['roundtrips' if crypto else 'operations']) != (5000 if crypto else 20000):
            raise BudgetViolation('Zero-contract workload coverage mismatch')
        seen.add(key)
        total_bytes(row)
    if seen != expected:
        raise BudgetViolation('Missing zero-contract samples')
    return len(seen)


def check_zero(rows, crypto):
    count = zero_rows(rows, crypto)
    if any(total_bytes(row) != 0 for row in rows):
        raise BudgetViolation('Exact zero-allocation contract exceeded')
    return count


def idle_samples(directory):
    identities = set()
    repetitions = []
    for repetition in (1, 2, 3):
        cohort, source, rows = read_csv(Path(directory) / f'idle-sctp-{repetition}.csv', 'profile')
        identities.add((cohort, source))
        groups = {}
        seen = set()
        for row in rows:
            mode, trace, pairs = row['channel_state'], row['trace'], int(row['pairs'])
            key = (mode, trace, pairs)
            sample = int(row['sample'])
            if row['profile'] == 'harness_only':
                if key != ('off', 'False', 0):
                    raise BudgetViolation('Invalid no-peer control')
            elif row['profile'] != 'AeadAes256Gcm' or key not in IDLE_GROUPS:
                raise BudgetViolation('Unreviewed idle workload')
            if sample not in range(3) or (key, sample) in seen:
                raise BudgetViolation('Idle sample coverage mismatch')
            seen.add((key, sample))
            for field in ('sctp_outstanding', 'sctp_buffered_send', 'sctp_buffered_receive', 'sctp_retransmissions', 'retransmissions'):
                if int(row[field]) != 0:
                    raise BudgetViolation('Idle workload was not drained or retransmission-free')
            if number(row['elapsed_ms']) < 1900:
                raise BudgetViolation('Idle measurement interval truncated')
            groups.setdefault(key, []).append(number(row['process_bytes_per_second']))
        expected = set(IDLE_GROUPS) | {('off', 'False', 0)}
        if set(groups) != expected or any(len(values) != 3 for values in groups.values()):
            raise BudgetViolation('Missing idle workload samples')
        repetitions.append({'/'.join(map(str, key)): statistics.median(values) for key, values in groups.items()})
    if len(identities) != 1:
        raise BudgetViolation('Mixed source or runtime/platform metadata')
    return *next(iter(identities)), repetitions


def ceiling(values):
    # Explicit noise allowance, never silently grown by validation or CI.
    proposed = max(values) * 1.5 + 8192
    if not math.isfinite(proposed):
        raise BudgetViolation('Unrepresentable calibration ceiling')
    return math.ceil(proposed / 8192) * 8192


def calibrate(directories, source):
    cohorts = {}
    for directory in directories:
        cohort, actual_source, repetitions = idle_samples(directory)
        if actual_source != source or cohort in cohorts:
            raise BudgetViolation('Calibration source mismatch or duplicate cohort')
        limits = {key: ceiling([repetitions[0][key], repetitions[1][key]]) for key in repetitions[0]}
        for key, value in repetitions[2].items():
            if value > limits[key]:
                raise BudgetViolation(f'Independent holdout exceeds proposed budget: {cohort}/{key}')
        cohorts[cohort] = {'ceilings_bytes_per_second': limits, 'training_processes': repetitions[:2], 'holdout_process': repetitions[2]}
    return {'schema_version': 1, 'source_commit': source,
            'policy': 'median_of_three_samples; train_processes_1_2; holdout_process_3; max_training_x1.5_plus8192_roundup8192; no_timing_gate',
            'cohorts': cohorts}


def check(directory, budgets, zero_only=False):
    directory = Path(directory)
    identities = set()
    count = 0
    for name, header, crypto in (('performance.csv', 'profile', True), ('packets.csv', 'operation', False)):
        cohort, source, rows = read_csv(directory / name, header)
        identities.add((cohort, source))
        count += check_zero(rows, crypto)
    if len(identities) != 1:
        raise BudgetViolation('Mixed zero-contract identities')
    cohort, source = next(iter(identities))
    if not zero_only:
        actual_cohort, actual_source, repetitions = idle_samples(directory)
        if (actual_cohort, actual_source) != (cohort, source):
            raise BudgetViolation('Idle and synchronous measurements differ in identity')
        if budgets.get('schema_version') != 1 or cohort not in budgets['cohorts']:
            raise BudgetViolation('Unreviewed runtime/platform cohort; retain results for explicit budget review')
        limits = budgets['cohorts'][cohort]['ceilings_bytes_per_second']
        for repetition in repetitions:
            if set(limits) != set(repetition):
                raise BudgetViolation('Budget workload coverage mismatch')
            for key, value in repetition.items():
                if value > number(limits[key]):
                    raise BudgetViolation(f'Idle allocation budget exceeded: {key}: {value} > {limits[key]}')
    return {'verdict': 'pass', 'source': source, 'cohort': cohort, 'exact_zero_samples': count,
            'idle_processes': 0 if zero_only else 3, 'limits_auto_updated': False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', nargs='?')
    parser.add_argument('--budgets', default='specs/performance-budgets.json')
    parser.add_argument('--zero-only', action='store_true')
    parser.add_argument('--calibrate', nargs='+')
    parser.add_argument('--source')
    parser.add_argument('--verify-positive-control')
    parser.add_argument('--output')
    args = parser.parse_args()
    try:
        if args.calibrate:
            if not args.source or not re.fullmatch('[0-9a-f]{40}', args.source):
                raise BudgetViolation('Calibration requires exact source revision')
            result = calibrate(args.calibrate, args.source)
        elif args.verify_positive_control:
            _, _, rows = read_csv(args.verify_positive_control, 'profile')
            zero_rows(rows, True)
            if any(total_bytes(row) < 256 for row in rows):
                raise BudgetViolation('Positive control did not observe deliberate allocation in every sample')
            try:
                check_zero(rows, True)
            except BudgetViolation as error:
                if str(error) != 'Exact zero-allocation contract exceeded':
                    raise
                result = {'verdict': 'pass', 'deliberate_allocation_rejected': True, 'samples': len(rows)}
            else:
                raise BudgetViolation('Positive control was incorrectly accepted')
        else:
            if not args.directory:
                raise BudgetViolation('Result directory required')
            budgets = {} if args.zero_only else json.loads(Path(args.budgets).read_text())
            result = check(args.directory, budgets, args.zero_only)
    except (ValueError, KeyError, OSError, TypeError) as error:
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
