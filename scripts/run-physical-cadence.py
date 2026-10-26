#!/usr/bin/env python3
"""Run under host admission; synthetic loopback only, no devices/providers/playback."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess

p=argparse.ArgumentParser(description=__doc__);p.add_argument('assembly',type=Path);p.add_argument('--source');p.add_argument('--manifest',type=Path)
p.add_argument('--artifact-only',action='store_true',help='Require a verified source manifest; no repository discovery or source copy')
p.add_argument('--runtime',type=Path);p.add_argument('--output',type=Path);args=p.parse_args()
root=Path('artifacts/physical-cadence-20261024');runtime=args.runtime or root/'runtime/dotnet';output=args.output or root/'canary'
if args.artifact_only and (not args.source or not args.manifest):raise ValueError('Standalone artifacts require exact source and manifest')
output.mkdir(exist_ok=False)
checkout_source=None if args.artifact_only else subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip()
source=args.source or checkout_source
if args.manifest:
    manifest=json.loads(args.manifest.read_text())
    if manifest['source']!=source:raise ValueError('Canary source manifest mismatch')
    for name,digest in manifest['sha256'].items():
        if Path(name).name!=name or hashlib.sha256((args.assembly.parent/name).read_bytes()).hexdigest()!=digest:raise ValueError('Canary binary manifest mismatch')
inputs=['src/libs/WebRTC','src/tests/WebRTC.Performance','src/tests/WebRTC.ProtocolTests/PacketAllocationChecks.cs','src/tests/WebRTC.ProtocolTests/RejectionAllocationChecks.cs','Directory.Build.props','Directory.Build.targets','global.json','NuGet.Config']
if not args.artifact_only and subprocess.check_output(['git','diff',source,checkout_source,'--',*inputs],text=True).strip():raise ValueError('Compiled canary inputs differ from the current checkout')
if not args.artifact_only and subprocess.check_output(['git','status','--porcelain'],text=True).strip():raise ValueError('Record a clean committed source before physical measurement')
env=dict(os.environ,GITHUB_SHA=source,TRYAGI_AUTH_LOAD_PACING='interval')
try: virtual=subprocess.check_output(['sysctl','-n','kern.hv_vmm_present'],text=True,stderr=subprocess.STDOUT).strip()
except subprocess.CalledProcessError: virtual='unavailable'
if platform.system()!='Darwin' or platform.machine()!='arm64' or virtual!='0':raise ValueError('Physical arm64 Mac evidence required')
proof=dict(source=source,canary_checkout_source=checkout_source,os=platform.platform(),architecture=platform.machine(),hypervisor_flag=virtual,
           scope='local Mac synthetic loopback nominal audio packet cadence; not speaker/Watch/provider/audible E2E',repetitions=[])
try:
    for repetition in range(1,4):
        result=dict(repetition=repetition)
        proof['repetitions'].append(result)
        for suite,name in (('timers','timers'),('auth-load','audio')):
            capture=output/f'{name}-{repetition}.csv';error=output/f'{name}-{repetition}.stderr'
            with capture.open('w') as out,error.open('w') as err:
                run=subprocess.run([str(runtime.resolve()),str(args.assembly.resolve()),'--suite',suite],env=env,stdout=out,stderr=err,timeout=180)
            result[suite+'_exit_code']=run.returncode
            if run.returncode:raise RuntimeError(f'Physical {suite} workload failed at repetition{repetition}; capture preserved')
            if suite=='auth-load':
                subprocess.run(['python3',str(Path(__file__).with_name('check-authentication-load.py')),str(capture),'--output',str(output/f'audio-{repetition}-proof.json')],check=True,timeout=30)
            else:
                subprocess.run(['python3',str(Path(__file__).with_name('check-timer-control.py')),str(capture),str(output/f'timers-{repetition}-proof.json')],check=True,timeout=30)
        for name in ('audio','timers'):
            if json.loads((output/f'{name}-{repetition}-proof.json').read_text())['source']!=source:raise ValueError('Observed source identity differs from verified payload')
finally:
    (output/'physical-proof.json').write_text(json.dumps(proof,indent=2)+'\n')
