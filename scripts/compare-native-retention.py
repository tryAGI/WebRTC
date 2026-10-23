#!/usr/bin/env python3
"""Compare same-source default/off controls; differences are not owner attribution."""
import argparse
import importlib.util
import json
from pathlib import Path
import statistics

spec = importlib.util.spec_from_file_location('retention', Path(__file__).with_name('summarize-native-retention.py'))
retention = importlib.util.module_from_spec(spec); spec.loader.exec_module(retention)
MODES = ('empty_control', 'crypto_only', 'identity_only', 'peer_audio', 'peer_open')


def compare(root, modes=MODES):
    records, identities = {}, set()
    for directory in sorted(Path(root).iterdir()):
        if not directory.is_dir() or not (directory / 'profiles').is_dir(): continue
        # Recompute from all raw numeric captures; never trust an editable verdict.
        profile = next((directory / 'profiles').glob('*/profile.json'), None)
        if profile is None: raise ValueError('Missing memory process captures')
        mode = json.loads(profile.read_text())['mode']
        if mode not in modes: raise ValueError('Unexpected memory workload')
        result = retention.summarize(directory / 'profiles', mode)
        cohort = result['cohort']; off = cohort.endswith('|tiered_compilation=0')
        base = cohort.removesuffix('|tiered_compilation=0')
        identities.add((result['source'], base))
        key = mode, 'off' if off else 'default'
        if key in records: raise ValueError('Duplicate memory control')
        records[key] = result
    if len(identities) != 1 or set(records) != {(mode, control) for mode in modes for control in ('default', 'off')}:
        raise ValueError('Incomplete or mismatched memory controls')
    comparisons = {}
    for mode in modes:
        pair = {}
        for control in ('default', 'off'):
            result = records[mode, control]
            names = result['delta_bytes']
            pair[control] = dict(delta_bytes=result['delta_bytes'], tail_bytes_per_wave=result['tail_bytes_per_wave'],
                final_phase_median_bytes={name: statistics.median(r['phases'][-1][name] for r in result['repetitions']) for name in names})
        pair['off_minus_default_final_median_bytes'] = {name: pair['off']['final_phase_median_bytes'][name] - pair['default']['final_phase_median_bytes'][name] for name in pair['default']['final_phase_median_bytes']}
        comparisons[mode] = pair
    source, cohort = next(iter(identities))
    return dict(source=source, cohort=cohort, workloads=comparisons, leak_freedom_verdict=False,
                scope='three fresh processes per workload/control; matching source and disposed phases; tiering-off is diagnostic only; malloc rounding and hosted-runner variance remain; differences do not attribute native owners or justify production settings')


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__); p.add_argument('directory'); p.add_argument('--mode', choices=('all', *MODES), default='all'); p.add_argument('--output', required=True)
    args = p.parse_args(); result = compare(args.directory, MODES if args.mode == 'all' else (args.mode,))
    Path(args.output).write_text(json.dumps(result, indent=2, sort_keys=True) + '\n')
