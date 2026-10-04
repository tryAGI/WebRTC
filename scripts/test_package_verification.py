"""Exercise the actual publication verifier against synthetic package mutations."""
import importlib.util
import tempfile
import unittest
import zipfile
from pathlib import Path

spec = importlib.util.spec_from_file_location("verify_package", Path(__file__).with_name("verify-package.py"))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
SHA = "a" * 40
MANIFEST = '''<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata>
<id>tryAGI.WebRTC</id><version>{version}</version><license type="expression">MIT</license>
<repository type="git" url="https://github.com/tryAGI/WebRTC" commit="''' + SHA + '''" />
{dependencies}</metadata></package>'''


class PackageVerification(unittest.TestCase):
    def package(self, path, version="0.1.0-dev.42", dependencies="", mutations=None):
        contents = {"tryAGI.WebRTC.nuspec": MANIFEST.format(version=version, dependencies=dependencies),
                    "lib/net10.0/tryAGI.WebRTC.dll": b"synthetic assembly", "README.md": "readme"}
        if mutations:
            mutations(contents)
        with zipfile.ZipFile(path / "test.nupkg", "w") as archive:
            for name, value in contents.items():
                archive.writestr(name, value)

    def test_stable_and_development_zero_major(self):
        for version in ["0.1.0", "0.1.1-dev.1", "0.2.0-rc.2"]:
            with self.subTest(version=version), tempfile.TemporaryDirectory() as scratch:
                path = Path(scratch)
                self.package(path, version)
                self.assertEqual(module.verify(path, SHA)["version"], version)

    def test_rejects_non_zero_major_and_invalid_versions(self):
        for version in ["1.0.0", "10.0.0-dev.1", "0.1", "0.1.0-", "0.1.0+unknown"]:
            with self.subTest(version=version), tempfile.TemporaryDirectory() as scratch:
                path = Path(scratch)
                self.package(path, version)
                with self.assertRaises(ValueError):
                    module.verify(path, SHA)

    def test_rejects_wrong_commit(self):
        with tempfile.TemporaryDirectory() as scratch:
            path = Path(scratch)
            self.package(path)
            with self.assertRaises(ValueError):
                module.verify(path, "b" * 40)

    def test_rejects_runtime_dependency(self):
        with tempfile.TemporaryDirectory() as scratch:
            path = Path(scratch)
            self.package(path, dependencies='<dependencies><group targetFramework="net10.0"><dependency id="Unreviewed" version="1.0.0" /></group></dependencies>')
            with self.assertRaises(ValueError):
                module.verify(path, SHA)

    def test_rejects_missing_readme_wrong_framework_native_and_tool_assets(self):
        def framework(files):
            files["lib/net9.0/tryAGI.WebRTC.dll"] = files.pop("lib/net10.0/tryAGI.WebRTC.dll")
        mutations = [lambda files: files.pop("README.md"), framework,
                     lambda files: files.update({"runtimes/linux-x64/native/test.so": b"native"}),
                     lambda files: files.update({"build/hidden.targets": b"tool"})]
        for mutate in mutations:
            with self.subTest(mutation=mutate), tempfile.TemporaryDirectory() as scratch:
                path = Path(scratch)
                self.package(path, mutations=mutate)
                with self.assertRaises(ValueError):
                    module.verify(path, SHA)

    def test_rejects_wrong_id_license_repository(self):
        for old, new in [("tryAGI.WebRTC</id>", "Other</id>"), (">MIT</license>", ">Unknown</license>"),
                         ("https://github.com/tryAGI/WebRTC", "https://example.invalid/other")]:
            with self.subTest(field=old), tempfile.TemporaryDirectory() as scratch:
                path = Path(scratch)
                self.package(path, mutations=lambda files: files.update({"tryAGI.WebRTC.nuspec": files["tryAGI.WebRTC.nuspec"].replace(old, new)}))
                with self.assertRaises(ValueError):
                    module.verify(path, SHA)


if __name__ == "__main__":
    unittest.main()
