#!/usr/bin/env python3
"""Admitted physical controls: process background cleared; native thread QoS varied."""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import platform
import subprocess


def module(name,file):
    spec=importlib.util.spec_from_file_location(name,Path(__file__).with_name(file));result=importlib.util.module_from_spec(spec);spec.loader.exec_module(result);return result


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('native',type=Path);p.add_argument('output',type=Path);args=p.parse_args()
    controls=module('physical','run-physical-scheduling.py');qos=module('qos','check-native-qos.py')
    manifest=controls.verified(args.native,'manifest.json',('scheduling','native-timers'))
    if platform.system()!='Darwin' or platform.machine()!='arm64' or subprocess.check_output(['sysctl','-n','kern.hv_vmm_present'],text=True).strip()!='0':raise ValueError('Physical arm64 Mac required')
    args.output.mkdir(exist_ok=False);proof=dict(manifest=manifest,host=platform.platform(),pairs=[],scope='600 native wakes; owned child process/thread only; no worker or global policy change; no timing acceptance waiver')
    try:
        for repetition in range(1,4):
            pair=dict(repetition=repetition);proof['pairs'].append(pair)
            for mode in (('default','user-initiated') if repetition%2 else ('user-initiated','default')):
                output=args.output/f'{mode}-{repetition}.csv';error=output.with_suffix('.stderr')
                env=dict(os.environ,GITHUB_SHA=manifest['source']);env.pop('TRYAGI_NATIVE_QOS',None)
                if mode=='user-initiated':env['TRYAGI_NATIVE_QOS']=mode
                with output.open('w') as out,error.open('w') as err:
                    result=subprocess.run([str((args.native/'scheduling').resolve()),'restored',str((args.native/'native-timers').resolve())],env=env,stdout=out,stderr=err,timeout=60)
                if result.returncode:raise ValueError('Native QoS child failed; raw files retained')
                source,summary=qos.read(output,mode)
                if source!='# native_source='+manifest['source']:raise ValueError('Native source mismatch')
                if mode=='user-initiated' and not summary['qos_identity'].endswith('observed=25'):raise ValueError('Requested QoS not observed')
                pair[mode]=dict(policy=controls.policy(error.read_text(),'restored'),**summary)
    finally:
        (args.output/'proof.json').write_text(json.dumps(proof,indent=2)+'\n')
