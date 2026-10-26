#!/usr/bin/env python3
"""Backport dotnet/runtime#124266 without changing the installed SDK or diagnostics."""
import argparse
from pathlib import Path
import re
import shutil
import xml.etree.ElementTree as ET


def prepare(entry, output):
    entry = Path(entry).resolve()
    source_build = entry.parent
    native = source_build / "Microsoft.NETCore.Native.targets"
    text = native.read_bytes().decode("utf-8")
    candidates = []
    for match in re.finditer(r'<Exec\b(?:"[^"]*"|[^">])*/>', text, re.S):
        node = ET.fromstring(match.group())
        if (node.get("Command", "").lstrip().startswith("dsymutil ")
                and "_IsApplePlatform" in node.get("Condition", "")):
            candidates.append((match, node))
    if len(candidates) != 1:
        raise ValueError("Unexpected NativeAOT symbol task layout; refusing an unreviewed patch")
    match, node = candidates[0]
    existing = node.get("IgnoreStandardErrorWarningFormat")
    if existing == "true":
        return entry
    if existing is not None:
        raise ValueError("Unexpected dsymutil diagnostic policy; refusing an unreviewed patch")

    # Same change as the upstream fix: preserve tool output and fail on a nonzero
    # exit status, rather than parsing Apple's successful dsymutil stderr as MSBuild errors.
    replacement = match.group().replace("<Exec", '<Exec IgnoreStandardErrorWarningFormat="true"', 1)
    patched = text[:match.start()] + replacement + text[match.end():]
    package = source_build.parent
    destination = Path(output).resolve() / package.name
    destination.mkdir(parents=True, exist_ok=True)
    for child in package.iterdir():
        if child.name == "build":
            continue
        link = destination / child.name
        if not link.exists() and not link.is_symlink():
            link.symlink_to(child, target_is_directory=child.is_dir())
    build = destination / "build"
    shutil.copytree(source_build, build, dirs_exist_ok=True)
    (build / native.name).write_bytes(patched.encode("utf-8"))
    return build / entry.name


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("entry", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    print(prepare(args.entry, args.output))
