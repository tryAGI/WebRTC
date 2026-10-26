#!/usr/bin/env python3
"""Run only under host admission: rotated, child-owned process policy controls."""
import argparse
import csv
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import platform
import re
import subprocess

spec=importlib.util.spec_from_file_location('clocks',Path(__file__).with_name('check-clock-isolation.py'))
clocks=importlib.util.module_from_spec(spec);spec.loader.exec_module(clocks)


def policy(text,mode):
    match=re.fullmatch(r'# scheduling_policy='+mode+r';before=([01]);after=([01])\n',text)
    if match is None:raise ValueError('Missing, ambiguous or diagnostic-contaminated child policy evidence')
    before,after=map(int,match.groups())
    expected=before if mode=='inherited' else 1 if mode=='background' else 0
    if after!=expected:raise ValueError('Requested process background state was not observed')
    return dict(before=before,after=after)


def verified(root,manifest_name,required):
    manifest=json.loads((root/manifest_name).read_text())
    if not re.fullmatch('[0-9a-f]{40}',manifest['source']) or not set(required)<=set(manifest['sha256']):raise ValueError('Incomplete committed payload identity')
    for name,digest in manifest['sha256'].items():
        if Path(name).name!=name or hashlib.sha256((root/name).read_bytes()).hexdigest()!=digest:raise ValueError('Payload manifest mismatch')
    return manifest


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('native',type=Path);p.add_argument('managed',type=Path);p.add_argument('runtime',type=Path);p.add_argument('output',type=Path);args=p.parse_args()
    native=verified(args.native,'manifest.json',('scheduling','native-timers'))
    managed=verified(args.managed,'canary-build.json',('WebRTC.Performance.dll','tryAGI.WebRTC.dll'))
    if platform.system()!='Darwin' or platform.machine()!='arm64' or subprocess.check_output(['sysctl','-n','kern.hv_vmm_present'],text=True).strip()!='0':raise ValueError('Owned physical arm64 Mac required')
    args.output.mkdir(exist_ok=False)
    proof=dict(native_manifest=native,managed_manifest=managed,host=platform.platform(),pairs=[],scope='physical child process background-policy controls; no global/worker/audio setting change, no timing acceptance waiver')
    try:
        modes=('inherited','background','restored')
        for repetition in range(3):
            for mode in modes[repetition:]+modes[:repetition]:
                pair=dict(repetition=repetition+1,mode=mode);proof['pairs'].append(pair)
                for kind in (('native','managed') if repetition%2==0 else ('managed','native')):
                    out=args.output/f'{mode}-{repetition+1}-{kind}.csv';err=out.with_suffix('.stderr')
                    target=[str((args.native/'native-timers').resolve())] if kind=='native' else [str(args.runtime.resolve()),str((args.managed/'WebRTC.Performance.dll').resolve()),'--suite','timers']
                    source=(native if kind=='native' else managed)['source']
                    with out.open('w') as stdout,err.open('w') as stderr:
                        result=subprocess.run([str((args.native/'scheduling').resolve()),mode,*target],env=dict(os.environ,GITHUB_SHA=source),stdout=stdout,stderr=stderr,timeout=100)
                    pair[kind+'_exit_code']=result.returncode
                    if result.returncode:raise ValueError('Child failed; preserve raw files')
                    pair[kind+'_policy']=policy(err.read_text(),mode)
                lines=(args.output/f'{mode}-{repetition+1}-native.csv').read_text().splitlines()
                if [s for s in lines if s.startswith('# native_source=')]!=['# native_source='+native['source']]:raise ValueError('Native source mismatch')
                rows=list(csv.DictReader(s for s in lines if not s.startswith('#')))
                cohort,source,managed_rows=clocks.timers.core.read_csv(args.output/f'{mode}-{repetition+1}-managed.csv','timer')
                if source!=managed['source']:raise ValueError('Managed source mismatch')
                pair.update(cohort=cohort,measurements=clocks.summarize(rows,managed_rows))
    finally:
        (args.output/'proof.json').write_text(json.dumps(proof,indent=2)+'\n')
