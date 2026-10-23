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
            for key, values in groups.items():
                results.setdefault(key, {})[mode + '_median_ns'] = statistics.median(values)
    if len(identities) != 1:
        raise core.BudgetViolation('Paired compiler measurements differ in source or hardware/runtime cohort')
    for result in results.values():
        ratio = result['aot_median_ns'] / result['jit_median_ns'] if result['jit_median_ns'] else None
        if ratio is not None and not math.isfinite(ratio): raise core.BudgetViolation('Nonfinite compiler comparison ratio')
        result['aot_over_jit'] = ratio
    cohort, source = next(iter(identities))
    return dict(source=source, cohort=cohort, timings=results, timing_gate=False,
                scope='same runner, sequential fresh processes; timing observations include hosted-runner noise; zero allocation checked separately')


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__); p.add_argument('directory'); p.add_argument('--output', required=True)
    a = p.parse_args(); result = compare(a.directory)
    Path(a.output).write_text(json.dumps(result, indent=2, sort_keys=True) + '\n')
