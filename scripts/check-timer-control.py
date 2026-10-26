#!/usr/bin/env python3
"""Validate complete raw timer controls without inventing a timing acceptance budget."""
import argparse
import importlib.util
import itertools
import json
import math
from pathlib import Path
import statistics
spec=importlib.util.spec_from_file_location('timer_core',Path(__file__).with_name('check-performance-budgets.py'))
core=importlib.util.module_from_spec(spec);spec.loader.exec_module(core)


def validate(rows):
    expected=set(itertools.product(('delay','periodic','sleep'),range(100)));seen=set();groups={k:[] for k in ('delay','periodic','sleep')}
    for row in rows:
        key=(row['timer'],int(row['sample']))
        if key not in expected or key in seen or float(row['requested_us'])!=20000: raise ValueError('Timer control coverage/request mismatch')
        elapsed=float(row['elapsed_us'])
        if not math.isfinite(elapsed) or elapsed<=0: raise ValueError('Nonfinite/nonpositive timer observation')
        seen.add(key);groups[key[0]].append(elapsed)
    if seen!=expected: raise ValueError('Incomplete timer control')
    return dict(modes={k:dict(samples=len(v),median_us=statistics.median(v),p95_us=sorted(v)[94],max_us=max(v),gaps_over_40ms=sum(x>40000 for x in v)) for k,v in groups.items()},
                scope='framework-only wake gaps; no sockets/crypto; observations, no timing acceptance budget; periodic ticks can coalesce')


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('csv');p.add_argument('output');args=p.parse_args()
    cohort,source,rows=core.read_csv(args.csv,'timer');result=validate(rows);result.update(cohort=cohort,source=source)
    Path(args.output).write_text(json.dumps(result,indent=2)+'\n')
