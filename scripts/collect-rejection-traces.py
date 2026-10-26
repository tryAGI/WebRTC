#!/usr/bin/env python3
"""Trace only bounded synthetic children; sampling is thread time, not OS CPU time."""
import argparse
import json
import os
from pathlib import Path
import subprocess

p=argparse.ArgumentParser(description=__doc__);p.add_argument('tool');p.add_argument('assembly');p.add_argument('output',type=Path);args=p.parse_args()
args.output.mkdir(parents=True,exist_ok=False)
proof=dict(tool_version='10.0.750501',modes=[],scope='EventPipe sampled managed thread stacks; no exception payload provider; not exclusive native CPU attribution or allocation acceptance')
for mode in ('valid','bad-tag','throw'):
    trace=args.output/(mode+'.nettrace')
    env=dict(os.environ,TRYAGI_COST_MODE=mode)
    with (args.output/(mode+'.log')).open('w') as log:
        result=subprocess.run([args.tool,'collect','--profile','dotnet-sampled-thread-time',
            '--providers','Microsoft-Windows-DotNETRuntime:0x18:4','--buffersize','16','--duration','00:00:20',
            '--output',str(trace),'--show-child-io','--','dotnet',args.assembly,'--suite','rejection-cost'],
            env=env,stdout=log,stderr=subprocess.STDOUT,timeout=60)
    if result.returncode: raise RuntimeError('Trace/workload failed: '+mode)
    if not trace.is_file() or not 0<trace.stat().st_size<=128*1024*1024: raise ValueError('Trace missing or over128MiB bound')
    # Preserve the tool's own report, with the same thread-time limitation.
    with (args.output/(mode+'-top.txt')).open('w') as report:
        subprocess.run([args.tool,'report',str(trace),'topN','-n','25'],stdout=report,stderr=subprocess.STDOUT,check=True,timeout=60)
    proof['modes'].append(dict(mode=mode,trace_bytes=trace.stat().st_size,workload_exit_code=result.returncode))
(args.output/'trace-proof.json').write_text(json.dumps(proof,indent=2)+'\n')
