#!/usr/bin/env python3
"""Fail closed on wrong-source, non-0.x, dependency-bearing or incomplete packages."""
import hashlib
import json
import re
import sys
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path


def verify(directory: Path, expected_sha: str) -> dict:
    if not re.fullmatch(r"[0-9a-f]{40}", expected_sha):
        raise ValueError("Expected an exact Git commit")
    packages = list(directory.glob("*.nupkg"))
    if len(packages) != 1:
        raise ValueError("Expected exactly one package")
    package = packages[0]
    with zipfile.ZipFile(package) as archive:
        specs = [n for n in archive.namelist() if n.endswith(".nuspec")]
        if len(specs) != 1:
            raise ValueError("Expected exactly one NuGet manifest")
        root = ET.fromstring(archive.read(specs[0]))
        ns = {"n": root.tag.split("}")[0][1:]}
        metadata = root.find("n:metadata", ns)
        if metadata is None:
            raise ValueError("Missing package metadata")
        def value(name: str) -> str:
            return metadata.findtext("n:" + name, default="", namespaces=ns)
        if value("id") != "tryAGI.WebRTC" or not re.fullmatch(r"0\.\d+\.\d+(?:-[0-9A-Za-z]+(?:\.[0-9A-Za-z]+)*)?", value("version")):
            raise ValueError("Wrong package identity or non-0.x version")
        license_node = metadata.find("n:license", ns)
        if license_node is None or license_node.get("type") != "expression" or license_node.text != "MIT":
            raise ValueError("Wrong package license")
        repository = metadata.find("n:repository", ns)
        if repository is None or repository.get("type") != "git" or repository.get("url") != "https://github.com/tryAGI/WebRTC" or repository.get("commit") != expected_sha:
            raise ValueError("Package does not declare the expected repository commit")
        if metadata.findall(".//n:dependency", ns):
            raise ValueError("Package unexpectedly acquired NuGet runtime dependencies")
        libraries = [n for n in archive.namelist() if n.startswith("lib/") and n.endswith(".dll")]
        if libraries != ["lib/net10.0/tryAGI.WebRTC.dll"] or "README.md" not in archive.namelist():
            raise ValueError("Wrong target framework, assembly or missing package readme")
        if any(n.startswith(("runtimes/", "tools/", "build/", "buildTransitive/")) or (n.lower().endswith((".so", ".dylib", ".exe", ".dll")) and n != "lib/net10.0/tryAGI.WebRTC.dll") for n in archive.namelist()):
            raise ValueError("Unexpected native, tool or build assets in runtime package")
        assembly = archive.read(libraries[0])
        return {"package": package.name, "version": value("version"), "repository_commit": expected_sha,
                "assembly_sha256": hashlib.sha256(assembly).hexdigest(), "runtime_nuget_dependencies": []}


if __name__ == "__main__":
    result = verify(Path(sys.argv[1]), sys.argv[2])
    (Path(sys.argv[1]) / "package-proof.json").write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps(result, indent=2))
