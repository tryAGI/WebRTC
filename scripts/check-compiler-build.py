#!/usr/bin/env python3
"""Require raw native/managed diagnostic cleanliness and identify the tested compiler."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess


def diagnostics(text):
    # Native link/archive warnings can be absent from MSBuild's summary. Keep
    # every original diagnostic visible and fail; filenames like error.cpp are
    # not diagnostic records and summary0counts are not an exemption.
    pattern=re.compile(r'(?i)(?:\b(?:warning|error)(?:\s+[a-z]+\d+)?\s*:|CMake\s+(?:Warning|Error)\b)')
    return [line for line in text.splitlines() if pattern.search(line)]


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('runtime',type=Path);p.add_argument('log',type=Path);p.add_argument('output',type=Path);args=p.parse_args()
    text=args.log.read_text();found=diagnostics(text)
    revision=subprocess.check_output(['git','-C',str(args.runtime),'rev-parse','HEAD'],text=True).strip()
    if revision!='6aa72daa24f66c01c3d7b6a32519e9d21396deec':raise ValueError('Unexpected compiler source commit')
    tools=args.runtime/'artifacts/bin/coreclr/osx.arm64.Release/ilc'
    binaries={}
    if tools.is_dir():
        for file in tools.rglob('*'):
            if file.is_file() and (file.name=='ilc' or file.suffix in ('.dll','.dylib','.json')):
                binaries[str(file.relative_to(tools))]=hashlib.sha256(file.read_bytes()).hexdigest()
    changes=subprocess.check_output(['git','-C',str(args.runtime),'diff'],text=True)
    proof=dict(source_revision=revision,source_diff_sha256=hashlib.sha256(changes.encode()).hexdigest(),
               build_log_sha256=hashlib.sha256(text.encode()).hexdigest(),raw_diagnostic_count=len(found),raw_diagnostics=found,
               compiler_files_sha256=binaries,scope='raw diagnostic gate and source-built compiler identity; no SDK replacement')
    args.output.write_text(json.dumps(proof,indent=2)+'\n')
    if found:
        print('\n'.join(found));raise SystemExit('Upstream source build emitted diagnostics; not clean acceptance')
    if 'ilc' not in binaries:raise SystemExit('Source compiler executable missing')
