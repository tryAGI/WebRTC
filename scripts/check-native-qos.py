#!/usr/bin/env python3
"""Check complete same-source native QoS controls; never change production priority."""
import argparse
import csv
import json
import math
from pathlib import Path
import statistics


def read(path,mode):
    lines=Path(path).read_text().splitlines();source=[s for s in lines if s.startswith('# native_source=')]
    qos=[s for s in lines if s.startswith('# native_qos_requested=')]
    if len(source)!=1 or len(qos)!=1 or not qos[0].startswith('# native_qos_requested='+mode+';observed='): raise ValueError('Missing/wrong native source/QoS identity')
    rows=list(csv.DictReader(s for s in lines if not s.startswith('#')));seen=set()
    for r in rows:
        i=int(r['sample']);values=[float(r[k]) for k in ('elapsed_us','wall_elapsed_us','monotonic_elapsed_us')]
        if r['timer']!='native-sleep' or i not in range(100) or i in seen or float(r['requested_us'])!=20000: raise ValueError('Native QoS coverage/request mismatch')
        if not all(math.isfinite(x) for x in values) or values[0]<=0 or values[2]<=0: raise ValueError('Invalid native QoS clocks')
        seen.add(i)
    if len(seen)!=100: raise ValueError('Incomplete native QoS capture')
    return source[0],dict(qos_identity=qos[0],median_raw_us=statistics.median(float(r['elapsed_us']) for r in rows),max_raw_us=max(float(r['elapsed_us']) for r in rows),
                          median_wall_us=statistics.median(float(r['wall_elapsed_us']) for r in rows))


def activity_identity(path, mode):
    markers=[line for line in Path(path).read_text().splitlines() if line.startswith('# native_activity=')]
    if markers!=['# native_activity='+mode]: raise ValueError('Missing/wrong/ambiguous activity identity')


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('baseline');p.add_argument('initiated');p.add_argument('output');p.add_argument('--activity');args=p.parse_args()
    a,baseline=read(args.baseline,'default');b,initiated=read(args.initiated,'user-initiated')
    if a!=b or 'working_tree_unverified' in a: raise ValueError('Same committed source required')
    result=dict(source=a,baseline=baseline,user_initiated=initiated,
        scope='synthetic process/thread only;100 wakes then exit; no global/permanent/managed/production policy change')
    if args.activity:
        c,activity=read(args.activity,'default')
        if c!=a: raise ValueError('Missing matching activity source')
        activity_identity(args.baseline,'off');activity_identity(args.initiated,'off');activity_identity(args.activity,'latency-critical')
        result['latency_critical_activity']=activity
    Path(args.output).write_text(json.dumps(result,indent=2)+'\n')
