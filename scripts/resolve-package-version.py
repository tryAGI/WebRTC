#!/usr/bin/env python3
"""Choose a registry-monotonic development package version."""
import json
import re
import subprocess
import sys
import urllib.request
from pathlib import Path


DEVELOPMENT_VERSION = re.compile(r"^(0\.\d+\.\d+-[0-9A-Za-z]+\.)(\d+)$")
REGISTRY_URL = "https://api.nuget.org/v3-flatcontainer/tryagi.webrtc/index.json"


def resolve_version(candidate: str, registry_versions: list[str]) -> str:
    match = DEVELOPMENT_VERSION.fullmatch(candidate)
    if match is None:
        raise ValueError("Main-branch publication requires a numeric 0.x development version")

    prefix, candidate_sequence = match.group(1), int(match.group(2))
    published_sequences = []
    for version in registry_versions:
        published_match = DEVELOPMENT_VERSION.fullmatch(version)
        if published_match is not None and published_match.group(1) == prefix:
            published_sequences.append(int(published_match.group(2)))

    if not published_sequences or candidate_sequence > max(published_sequences):
        return candidate
    return f"{prefix}{max(published_sequences) + 1}"


def registry_versions() -> list[str]:
    with urllib.request.urlopen(REGISTRY_URL, timeout=30) as response:
        payload = response.read(1024 * 1024 + 1)
    if len(payload) > 1024 * 1024:
        raise ValueError("NuGet version index exceeds the bounded response size")
    document = json.loads(payload)
    versions = document.get("versions")
    if not isinstance(versions, list) or not all(isinstance(value, str) for value in versions):
        raise ValueError("NuGet version index has an invalid shape")
    return versions


def project_version(project: Path, runner=subprocess.run) -> str:
    result = runner(
        [
            "dotnet", "msbuild", str(project), "-nologo", "-restore", "-t:MinVer",
            "-getProperty:PackageVersion",
        ],
        check=True,
        capture_output=True,
        text=True,
    )
    output = result.stdout.strip()
    if output.startswith("{"):
        value = json.loads(output).get("Properties", {}).get("PackageVersion")
    else:
        value = output
    if not isinstance(value, str) or not value:
        raise ValueError("MinVer did not return a package version")
    return value


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("Usage: resolve-package-version.py PROJECT")
    print(resolve_version(project_version(Path(sys.argv[1])), registry_versions()))
