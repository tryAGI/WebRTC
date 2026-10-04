#!/usr/bin/env python3
"""Execute the exact verified nupkg DLL; never rebuild the library to prove its identity."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import zipfile

spec = importlib.util.spec_from_file_location("verify_package", Path(__file__).with_name("verify-package.py"))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def verify_runtime(directory: Path, expected_sha: str) -> dict:
    (directory / "package-runtime-proof.json").unlink(missing_ok=True)
    proof = module.verify(directory, expected_sha)
    package, = directory.glob("*.nupkg")
    project = Path(__file__).resolve().parents[1] / "src/tests/WebRTC.PackageIdentity/WebRTC.PackageIdentity.csproj"
    with tempfile.TemporaryDirectory(prefix="webrtc-package-identity-") as scratch:
        scratch = Path(scratch)
        assembly = scratch / "tryAGI.WebRTC.dll"
        with zipfile.ZipFile(package) as archive:
            # Fixed member, not extractall; other package paths are never materialized.
            assembly.write_bytes(archive.read("lib/net10.0/tryAGI.WebRTC.dll"))
        env = os.environ.copy()
        env["WEBRTC_EXPECTED_PACKAGE_VERSION"] = proof["version"]
        env["WEBRTC_EXPECTED_SOURCE_REVISION"] = expected_sha
        command = ["dotnet", "run", "--project", str(project), "-c", "Release",
                   "--artifacts-path", str(scratch / "build"),
                   "--property:VerifiedPackageAssembly=" + str(assembly)]
        subprocess.run(command, cwd=project.parents[3], env=env, check=True, timeout=180)
        # Exercise both assertions against the same compiled consumer and package DLL.
        # A shape-only implementation or ignored expected input must not publish a proof.
        wrong_revision = ("1" if expected_sha[0] == "0" else "0") + expected_sha[1:]
        for name, value in [("WEBRTC_EXPECTED_PACKAGE_VERSION", proof["version"] + ".wrong"),
                            ("WEBRTC_EXPECTED_SOURCE_REVISION", wrong_revision)]:
            negative_env = dict(env, **{name: value})
            rejected = subprocess.run(command + ["--no-build"], cwd=project.parents[3],
                                      env=negative_env, capture_output=True, text=True, timeout=30)
            if rejected.returncode != 1 or "FAIL packaged library identity differs" not in rejected.stderr:
                raise ValueError("Packaged identity gate failed its wrong-input negative control")
        print("PASS packaged identity rejects wrong version and source revision")
    result = {"version": proof["version"], "repository_commit": expected_sha,
              "assembly_sha256": proof["assembly_sha256"],
              "runtime_identity_verified": True, "retained_after_disposal": True,
              "wrong_version_rejected": True, "wrong_revision_rejected": True}
    (directory / "package-runtime-proof.json").write_text(json.dumps(result, indent=2) + "\n")
    return result


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit("Usage: verify-package-identity.py PACKAGE_DIRECTORY EXPECTED_COMMIT")
    print(json.dumps(verify_runtime(Path(sys.argv[1]), sys.argv[2]), indent=2))
