#!/usr/bin/env python3
"""Run under host admission; synthetic loopback only, no devices/providers/playback."""
import argparse
import json
import os
from pathlib import Path
import platform
import subprocess

p=argparse.ArgumentParser(description=__doc__);p.add_argument('assembly',type=Path);args=p.parse_args()
root=Path('artifacts/physical-cadence-20261024');runtime=root/'runtime/dotnet';output=root/'canary'
output.mkdir(exist_ok=False)
source=subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip()
if subprocess.check_output(['git','status','--porcelain'],text=True).strip():raise ValueError('Record a clean committed source before physical measurement')
env=dict(os.environ,GITHUB_SHA=source,TRYAGI_AUTH_LOAD_PACING='interval')
try: virtual=subprocess.check_output(['sysctl','-n','kern.hv_vmm_present'],text=True,stderr=subprocess.STDOUT).strip()
except subprocess.CalledProcessError: virtual='unavailable'
proof=dict(source=source,os=platform.platform(),architecture=platform.machine(),hypervisor_flag=virtual,
           scope='local Mac synthetic loopback nominal audio packet cadence; not speaker/Watch/provider/audible E2E',repetitions=[])
try:
    for repetition in range(1,4):
        result=dict(repetition=repetition)
        for suite,name in (('timers','timers'),('auth-load','audio')):
            capture=output/f'{name}-{repetition}.csv';error=output/f'{name}-{repetition}.stderr'
            with capture.open('w') as out,error.open('w') as err:
                run=subprocess.run([str(runtime.resolve()),str(args.assembly.resolve()),'--suite',suite],env=env,stdout=out,stderr=err,timeout=180)
            result[suite+'_exit_code']=run.returncode
            if run.returncode:raise RuntimeError(f'Physical {suite} workload failed at repetition{repetition}; capture preserved')
            if suite=='auth-load':
                subprocess.run(['python3','scripts/check-authentication-load.py',str(capture),'--output',str(output/f'audio-{repetition}-proof.json')],check=True,timeout=30)
            else:
                subprocess.run(['python3','scripts/check-timer-control.py',str(capture),str(output/f'timers-{repetition}-proof.json')],check=True,timeout=30)
        proof['repetitions'].append(result)
finally:
    (output/'physical-proof.json').write_text(json.dumps(proof,indent=2)+'\n')
