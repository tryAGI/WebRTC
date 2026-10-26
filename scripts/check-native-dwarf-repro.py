#!/usr/bin/env python3
"""Preserve verifier diagnostics/counts for the independent framework-only repro."""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys

p = argparse.ArgumentParser(description=__doc__); p.add_argument('symbols'); p.add_argument('output'); args = p.parse_args()
result = subprocess.run(['xcrun', 'dwarfdump', '--verify', args.symbols], capture_output=True, text=True, timeout=120)
# These are original diagnostic streams, forwarded in full, not suppressed.
sys.stdout.write(result.stdout); sys.stderr.write(result.stderr)
text = result.stdout + result.stderr
errors = Counter(re.findall(r'(?m)^error:\s*([^\n]+)', text)); warnings = Counter(re.findall(r'(?m)^warning:\s*([^\n]+)', text))
proof = dict(exit_code=result.returncode, errors=dict(errors), warnings=dict(warnings), diagnostic_text_sha256=hashlib.sha256(text.encode()).hexdigest(),
             scope='authored minimal nested-EH application; framework only; no WebRTC or providers; verifier failure remains failure')
Path(args.output).write_text(json.dumps(proof, indent=2) + '\n')
if result.returncode or errors or warnings: raise SystemExit(1)
