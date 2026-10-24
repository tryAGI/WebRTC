#!/usr/bin/env python3
"""Validate authenticated load coverage and a real deliberate packet-loss control."""
import argparse
import importlib.util
import itertools
import json
from pathlib import Path

spec = importlib.util.spec_from_file_location('budgets', Path(__file__).with_name('check-performance-budgets.py'))
core = importlib.util.module_from_spec(spec); spec.loader.exec_module(core)


class DeliveryViolation(ValueError): pass


def validate(rows, control=False):
    expected = set(itertools.product(('AeadAes128Gcm',) if control else core.PROFILES, (0,) if control else (0, 16, 64, 256), range(3)))
    seen = set(); delivery = []
    for row in rows:
        key = (row['profile'], int(row['burst']), int(row['sample']))
        if key not in expected or key in seen: raise ValueError('Authentication load coverage mismatch')
        seen.add(key)
        values = {name: core.number(value) for name, value in row.items() if name != 'profile'}
        integers = ('burst', 'sample', 'offered', 'received', 'missing', 'injected', 'rejected', 'ice_drops', 'media_drops', 'fixture_drops', 'process_bytes', 'gen0', 'gen1', 'gen2')
        if any(not values[name].is_integer() for name in integers): raise ValueError('Nonintegral authentication counter')
        if values['offered'] != 100 or values['received'] + values['missing'] != 100 or values['received'] == 0:
            raise ValueError('Invalid accepted traffic accounting')
        if values['injected'] != values['burst'] * 100 or values['rejected'] > values['injected']:
            raise ValueError('Invalid injected/rejected packet accounting')
        if values['elapsed_ms'] <= 0 or not 0 < values['p50_ns'] <= values['p95_ns'] <= values['p99_ns']:
            raise ValueError('Invalid latency measurement')
        if control:
            if values['fixture_drops'] != (1 if key[2] == 0 else 0): raise ValueError('Real packet-loss control missing')
        elif values['fixture_drops'] != 0: raise ValueError('Unexpected fixture loss')
        if key[1] == 0 and (values['missing'] or values['ice_drops'] or values['media_drops']): delivery.append(key)
    if seen != expected: raise ValueError('Incomplete authentication load samples')
    if control:
        if delivery != [('AeadAes128Gcm', 0, 0)]: raise ValueError('Control failed to isolate baseline delivery gate')
        return dict(rows=len(rows), real_packet_drop_detected=True, violated_gate='baseline_delivery', security_verdict='harness rejects any observed corrupt plaintext; not a security audit')
    if delivery: raise DeliveryViolation('Baseline valid-packet delivery failed')
    return dict(rows=len(rows), baseline_delivery=True, treatment_losses=sum(int(r['missing']) for r in rows if int(r['burst']) > 0),
        scope='local authenticated UDP; process allocations include proxy/harness; rejected may be below injected due to kernel/transport drops; received-only latency is censored by loss; no absolute latency budget')


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__); p.add_argument('csv'); p.add_argument('--control', action='store_true'); p.add_argument('--output', required=True)
    args = p.parse_args(); cohort, source, rows = core.read_csv(args.csv, 'profile')
    result = validate(rows, args.control); result.update(cohort=cohort, source=source)
    Path(args.output).write_text(json.dumps(result, indent=2, sort_keys=True) + '\n')
