#!/usr/bin/env python3
"""Pair complete native/managed wake observations without imposing a latency waiver."""
import argparse
import csv
import importlib.util
import json
import math
from pathlib import Path
import statistics
spec=importlib.util.spec_from_file_location('timers',Path(__file__).with_name('check-timer-control.py'))
timers=importlib.util.module_from_spec(spec);spec.loader.exec_module(timers)


def summarize(native, managed):
    result=timers.validate(managed);seen=set()
    if len(native)!=100: raise ValueError('Native wake coverage incomplete')
    for row in native:
        key=int(row['sample'])
        if row['timer']!='native-sleep' or key not in range(100) or key in seen or float(row['requested_us'])!=20000:
            raise ValueError('Native wake coverage/request mismatch')
        seen.add(key)
    groups={'native-sleep':native}
    groups.update({kind:[r for r in managed if r['timer']==kind] for kind in ('delay','periodic','sleep')})
    clocks={}
    for kind,rows in groups.items():
        pairs=[(float(r['elapsed_us']),float(r['wall_elapsed_us'])) for r in rows]
        if any(not math.isfinite(a) or not math.isfinite(b) or a<=0 for a,b in pairs): raise ValueError('Invalid dual-clock observation')
        entry=dict(samples=len(rows),median_raw_us=statistics.median(a for a,b in pairs),max_raw_us=max(a for a,b in pairs),
                   median_wall_us=statistics.median(b for a,b in pairs),median_wall_to_raw_ratio=statistics.median(b/a for a,b in pairs),
                   max_absolute_wall_raw_difference_us=max(abs(a-b) for a,b in pairs),negative_wall_deltas=sum(b<0 for a,b in pairs))
        if kind=='native-sleep':
            deltas=[float(r['monotonic_elapsed_us']) for r in rows]
            if any(not math.isfinite(x) or x<=0 for x in deltas): raise ValueError('Invalid native monotonic clock')
            entry['median_monotonic_us']=statistics.median(deltas)
        clocks[kind]=entry
    result.update(clocks=clocks,scope='same-runner independent clocks/native sleep; measured host controls, not a production or audible SLO')
    return result


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('native');p.add_argument('managed');p.add_argument('output');args=p.parse_args()
    lines=Path(args.native).read_text().splitlines()
    cohort,source,managed=timers.core.read_csv(args.managed,'timer')
    if [x for x in lines if x.startswith('# native_source=')]!=['# native_source='+source]: raise ValueError('Native/managed committed source mismatch')
    native=list(csv.DictReader(x for x in lines if not x.startswith('#')))
    result=summarize(native,managed);result.update(cohort=cohort,source=source)
    Path(args.output).write_text(json.dumps(result,indent=2)+'\n')
