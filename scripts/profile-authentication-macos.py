#!/usr/bin/env python3
"""Bounded synthetic bad-tag stack capture; retain workload failure independently."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import time

p=argparse.ArgumentParser(description=__doc__); p.add_argument('assembly'); p.add_argument('output',type=Path); args=p.parse_args()
args.output.mkdir(parents=True,exist_ok=False)
with (args.output/'load.csv').open('w') as out, (args.output/'workload.stderr').open('w') as err:
    child=subprocess.Popen(['dotnet',args.assembly,'--suite','auth-load'],stdout=out,stderr=err,
                           env=dict(os.environ,TRYAGI_AUTH_LOAD_PACING='interval'))
    captures=[]; started=time.monotonic(); observer_failure=None
    try:
        for burst in (64,256):
            marker=f'# auth_load_phase=AeadAes128Gcm;burst={burst}'
            while marker not in (args.output/'load.csv').read_text():
                if child.poll() is not None: break
                if time.monotonic()-started>240: raise TimeoutError('Authentication phase wait deadline')
                time.sleep(.1)
            if child.poll() is not None: break
            # sample is a bounded observation of this synthetic child only. Its
            # own failure cannot turn a workload failure into successful evidence.
            result=subprocess.run(['sample',str(child.pid),'3','1','-file',str(args.output/f'burst-{burst}.sample.txt')],
                                  capture_output=True,text=True,timeout=20)
            print(result.stdout,end=''); print(result.stderr,end='')
            captures.append(dict(profile='AeadAes128Gcm',burst=burst,sample_exit_code=result.returncode,duration_seconds=3))
        code=child.wait(timeout=max(1,420-(time.monotonic()-started)))
    except (TimeoutError, subprocess.TimeoutExpired) as error:
        observer_failure=type(error).__name__
        raise
    finally:
        if child.poll() is None:
            child.terminate()
            try: child.wait(timeout=5)
            except subprocess.TimeoutExpired: child.kill(); child.wait(timeout=5)
        proof=dict(workload_exit_code=child.returncode,captures=captures,observer_failure=observer_failure,
                   observer_bound_seconds=420,scenario_deadline_seconds=30,
                   scope='synthetic loopback only; instrumentation changes scheduling; stacks are sampled/inclusive and cannot be summed as exclusive CPU cost')
        (args.output/'profile-proof.json').write_text(json.dumps(proof,indent=2)+'\n')
if code or len(captures)!=2 or any(c['sample_exit_code'] for c in captures): raise SystemExit(1)
