"""Immutable-version retry and secret-safe failure regressions, without a registry/key."""
import importlib.util
import io
import os
import runpy
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch
import urllib.error

from test_package_verification import MANIFEST, SHA
import zipfile

spec = importlib.util.spec_from_file_location("publish_package", Path(__file__).with_name("publish-package.py"))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def package_bytes(assembly=b"synthetic assembly", commit=SHA):
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w") as archive:
        archive.writestr("test.nuspec", MANIFEST.format(version="0.1.0", dependencies="").replace(SHA, commit))
        archive.writestr("lib/net10.0/tryAGI.WebRTC.dll", assembly)
        archive.writestr("README.md", "readme")
    return output.getvalue()


class PublishRetry(unittest.TestCase):
    def test_existing_version_requires_same_source_and_assembly(self):
        with tempfile.TemporaryDirectory() as scratch:
            path = Path(scratch)
            (path / "test.nupkg").write_bytes(package_bytes())
            proof = module.module.verify(path, SHA)
            with patch.object(module.urllib.request, "urlopen", return_value=io.BytesIO(package_bytes())):
                self.assertTrue(module.existing_matches(SHA, proof))
            for payload in [package_bytes(b"different assembly"), package_bytes(commit="b" * 40)]:
                with self.subTest(payload=payload), patch.object(module.urllib.request, "urlopen", return_value=io.BytesIO(payload)):
                    with self.assertRaises(ValueError):
                        module.existing_matches(SHA, proof)

    def test_only_not_found_authorizes_upload(self):
        proof = {"version": "0.1.0"}
        for status in [404, 403, 429, 500]:
            error = urllib.error.HTTPError("https://example.invalid", status, "synthetic error", {}, None)
            with self.subTest(status=status), patch.object(module.urllib.request, "urlopen", side_effect=error):
                if status == 404:
                    self.assertFalse(module.existing_matches(SHA, proof))
                else:
                    with self.assertRaises(urllib.error.HTTPError):
                        module.existing_matches(SHA, proof)

    def test_push_timeout_does_not_expose_key_in_exception(self):
        fake_key = "synthetic-secret-never-a-real-key"
        with tempfile.TemporaryDirectory() as scratch:
            path = Path(scratch)
            (path / "test.nupkg").write_bytes(package_bytes())
            error = urllib.error.HTTPError("https://example.invalid", 404, "missing", {}, None)
            with patch.dict(os.environ, {"NUGET_KEY": fake_key}), patch.object(sys, "argv", ["publish-package.py", str(path), SHA]), \
                 patch("urllib.request.urlopen", side_effect=error), \
                 patch("subprocess.run", side_effect=subprocess.TimeoutExpired(["dotnet", "--api-key", fake_key], 180)):
                with self.assertRaises(SystemExit) as raised:
                    runpy.run_path(str(Path(__file__).with_name("publish-package.py")), run_name="__main__")
                self.assertNotIn(fake_key, str(raised.exception))
                self.assertIn("timed out", str(raised.exception))
