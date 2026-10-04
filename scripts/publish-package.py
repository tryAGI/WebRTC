#!/usr/bin/env python3
"""Publish only verified packages; reruns accept an existing identical assembly/source."""
import os
import subprocess
import sys
import tempfile
import urllib.error
import urllib.request
from pathlib import Path
from importlib.util import module_from_spec, spec_from_file_location

spec = spec_from_file_location("verify_package", Path(__file__).with_name("verify-package.py"))
module = module_from_spec(spec)
spec.loader.exec_module(module)


def existing_matches(expected_sha: str, proof: dict) -> bool:
    version = proof["version"].lower()
    url = f"https://api.nuget.org/v3-flatcontainer/tryagi.webrtc/{version}/tryagi.webrtc.{version}.nupkg"
    try:
        with urllib.request.urlopen(url, timeout=30) as response:
            payload = response.read(20 * 1024 * 1024 + 1)
    except urllib.error.HTTPError as error:
        if error.code == 404:
            return False
        raise
    if len(payload) > 20 * 1024 * 1024:
        raise ValueError("Existing registry package exceeds size limit")
    with tempfile.TemporaryDirectory() as scratch:
        path = Path(scratch)
        (path / "existing.nupkg").write_bytes(payload)
        existing = module.verify(path, expected_sha)
    if existing["version"] != proof["version"] or existing["assembly_sha256"] != proof["assembly_sha256"]:
        raise ValueError("Immutable version already contains different source/assembly")
    print("Registry already contains this verified version/source/assembly")
    return True


if __name__ == "__main__":
    directory, sha = Path(sys.argv[1]), sys.argv[2]
    proof = module.verify(directory, sha)
    key = os.environ.get("NUGET_KEY", "")
    if not key:
        raise SystemExit("NUGET_KEY is unavailable")
    if not existing_matches(sha, proof):
        # Capture output so an unexpected tool diagnostic cannot echo the credential.
        try:
            result = subprocess.run(["dotnet", "nuget", "push", str(directory / proof["package"]),
                                     "--api-key", key, "--source", "https://api.nuget.org/v3/index.json"],
                                    capture_output=True, text=True, timeout=180)
        except subprocess.TimeoutExpired:
            raise SystemExit("NuGet push timed out; check registry state before retrying") from None
        print((result.stdout + result.stderr).replace(key, "[redacted]"))
        if result.returncode:
            raise SystemExit(result.returncode)
