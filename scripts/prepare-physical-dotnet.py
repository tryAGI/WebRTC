#!/usr/bin/env python3
"""Private, hash-verified Microsoft runtime for matched physical-host diagnostics."""
import hashlib
import io
import json
from pathlib import Path
import platform
import ssl
import tarfile
import urllib.request

VERSION='10.0.13'
ROOT=Path('artifacts/physical-cadence-20261024/runtime')
if platform.system()!='Darwin' or platform.machine()!='arm64':raise ValueError('Owned arm64 Mac control required')
if ROOT.exists():raise ValueError('Preserve existing runtime; do not overwrite a diagnostic consumer')

def get(url,limit):
    if not url.startswith('https://builds.dotnet.microsoft.com/dotnet/'):raise ValueError('Official Microsoft distribution required')
    with urllib.request.urlopen(url,timeout=60,context=ssl.create_default_context(cafile='/etc/ssl/cert.pem')) as response:
        data=response.read(limit+1)
    if len(data)>limit:raise ValueError('Diagnostic download bound exceeded')
    return data

metadata=json.loads(get('https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json',16*1024*1024))
release=next(r for r in metadata['releases'] if r['release-version']==VERSION)
entry=next(f for f in release['runtime']['files'] if f['rid']=='osx-arm64' and f['name'].endswith('.tar.gz'))
archive=get(entry['url'],64*1024*1024)
actual=hashlib.sha512(archive).hexdigest()
if actual.lower()!=entry['hash'].lower():raise ValueError('Official runtime hash mismatch')
ROOT.mkdir(parents=True)
with tarfile.open(fileobj=io.BytesIO(archive),mode='r:gz') as tar:
    tar.extractall(ROOT,filter='data')
(ROOT.parent/'runtime-proof.json').write_text(json.dumps(dict(version=VERSION,url=entry['url'],sha512=actual,
    tls_trust='/etc/ssl/cert.pem',scope='private published runtime only; no PATH/SDK/adv installation or activation'),indent=2)+'\n')
print('Verified private Microsoft runtime',VERSION)
