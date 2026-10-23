#!/usr/bin/env python3
"""Describe paired same-runner JIT/AOT timings; allocation guards decide acceptance."""
import argparse
import importlib.util
import json
import math
from pathlib import Path
import statistics

spec = importlib.util.spec_from_file_location('core', Path(__file__).with_name('check-performance-budgets.py'))
core = importlib.util.module_from_spec(spec)
spec.loader.exec_module(core)


def read_warmups(path, expected):
    records = {}
    for line in Path(path).read_text(encoding='utf-8-sig').splitlines():
        if not line.startswith('#warmup,'): continue
        _, key, rounds, elapsed, minimum, maximum, stable = line.split(',')
        rounds = int(rounds); elapsed = core.number(elapsed)
        minimum = core.number(minimum); maximum = core.number(maximum)
        if key not in expected or key in records or not 5 <= rounds <= 128 or elapsed <= 0 or maximum < minimum or stable not in ('True', 'False'):
            raise ValueError('Invalid timing warmup record')
        if stable == 'True' and (elapsed < 500 or minimum <= 0 or maximum / minimum > 1.11):
            raise ValueError('Unsubstantiated timing stability')
        records[key] = dict(rounds=rounds, elapsed_ms=elapsed, minimum_ns=minimum, maximum_ns=maximum, observed_stable=stable == 'True')
    if set(records) != expected: raise ValueError('Missing timing warmup observations')
    return records


def compare(root):
    results, identities = {}, set()
    for mode, expected_dynamic in (('jit', 'True'), ('aot', 'False')):
        for name, header, crypto in (('performance.csv', 'profile', True), ('packets.csv', 'operation', False)):
            cohort, source, rows = core.read_csv(Path(root) / mode / name, header)
            parts = cohort.split('|')
            if parts[-1] != 'dynamic_code=' + expected_dynamic:
                raise core.BudgetViolation('Compiler mode metadata mismatch')
            identities.add(('|'.join(parts[:-1]), source))
            core.check_zero(rows, crypto)
            groups = {}
            for row in rows:
                key = row['profile'] + '/' + row['payload_bytes'] if crypto else row['operation']
                groups.setdefault(key, []).append(core.number(row['ns_per_roundtrip' if crypto else 'ns_per_op']))
            warmups = read_warmups(Path(root) / mode / name, set(groups))
            for key, values in groups.items():
                result = results.setdefault(key, {})
                result[mode + '_median_ns'] = statistics.median(values)
                result[mode + '_warmup'] = warmups[key]
                result[mode + '_sample_spread'] = max(values) / min(values) if min(values) > 0 else None
    if len(identities) != 1:
        raise core.BudgetViolation('Paired compiler measurements differ in source or hardware/runtime cohort')
    for result in results.values():
        comparable = result['jit_warmup']['observed_stable'] and result['aot_warmup']['observed_stable']
        ratio = result['aot_median_ns'] / result['jit_median_ns'] if comparable and result['jit_median_ns'] else None
        if ratio is not None and not math.isfinite(ratio): raise core.BudgetViolation('Nonfinite compiler comparison ratio')
        result['aot_over_jit'] = ratio
    cohort, source = next(iter(identities))
    return dict(source=source, cohort=cohort, timings=results, timing_gate=False,
                scope='same runner, sequential fresh processes; bounded warmup observes stability, not JIT completion; no ratio for unstable warmup; hosted-runner noise remains; zero allocation checked separately')


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__); p.add_argument('directory'); p.add_argument('--output', required=True)
    a = p.parse_args(); result = compare(a.directory)
    Path(a.output).write_text(json.dumps(result, indent=2, sort_keys=True) + '\n')
