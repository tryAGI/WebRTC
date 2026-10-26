#!/usr/bin/env python3
"""Exercise the backport against real MSBuild, including a failing tool exit."""
import importlib.util
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("apple_targets", Path(__file__).with_name("patch-apple-native-aot-targets.py"))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class AppleNativeTargetsTests(unittest.TestCase):
    def test_stderr_is_retained_but_tool_exit_status_controls_success(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            build = root / "package" / "10.0.14" / "build"
            build.mkdir(parents=True)
            entry = build / "Microsoft.DotNet.ILCompiler.SingleEntry.targets"
            entry.write_text('<Project><Import Project="Microsoft.NETCore.Native.targets" /></Project>')
            native = build / "Microsoft.NETCore.Native.targets"
            original = '''<Project><Target Name="Probe">
<Exec Condition="'$(_IsApplePlatform)' == 'true'" Command="dsymutil fixture &amp;&amp; strip fixture" />
</Target></Project>'''
            native.write_text(original)
            tools = root / "bin"
            tools.mkdir()
            for name, content in {
                "dsymutil": '#!/bin/sh\necho "error: Apple module-cache fixture" >&2\nexit "${DSYMUTIL_TEST_EXIT:-0}"\n',
                "strip": '#!/bin/sh\nexit 0\n',
            }.items():
                p = tools / name
                p.write_text(content)
                p.chmod(0o755)
            env = os.environ.copy()
            env["PATH"] = str(tools) + os.pathsep + env["PATH"]
            env["DSYMUTIL_TEST_EXIT"] = "0"

            def run(target):
                return subprocess.run(["dotnet", "msbuild", str(target), "-nologo", "-t:Probe", "-warnaserror", "-p:_IsApplePlatform=true"],
                                      capture_output=True, text=True, env=env)

            self.assertNotEqual(0, run(entry).returncode, "Unpatched false positive must reproduce")
            patched = module.prepare(entry, root / "isolated")
            self.assertEqual(original, native.read_text(), "Installed targets must remain untouched")
            success = run(patched)
            self.assertEqual(0, success.returncode, success.stdout + success.stderr)
            self.assertIn("error: Apple module-cache fixture", success.stdout + success.stderr)
            env["DSYMUTIL_TEST_EXIT"] = "7"
            failed = run(patched)
            self.assertNotEqual(0, failed.returncode, "A real dsymutil failure must remain fatal")

    def test_unknown_layout_fails_closed(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            entry = root / "entry.targets"
            entry.write_text("<Project />")
            (root / "Microsoft.NETCore.Native.targets").write_text("<Project />")
            with self.assertRaises(ValueError):
                module.prepare(entry, root / "output")

    def test_upstream_fixed_targets_are_reused_unchanged(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            entry = root / "entry.targets"
            entry.write_text("<Project />")
            (root / "Microsoft.NETCore.Native.targets").write_text('''<Project>
<Exec Condition="'$(_IsApplePlatform)' == 'true'" IgnoreStandardErrorWarningFormat="true" Command="dsymutil fixture" />
</Project>''')
            self.assertEqual(entry.resolve(), module.prepare(entry, root / "output"))


if __name__ == "__main__":
    unittest.main()
