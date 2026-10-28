import importlib.util
import subprocess
import unittest
from pathlib import Path


spec = importlib.util.spec_from_file_location(
    "resolve_package_version", Path(__file__).with_name("resolve-package-version.py")
)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class ResolvePackageVersionTests(unittest.TestCase):
    def test_preserves_candidate_above_registry(self):
        self.assertEqual(
            "0.2.10-dev.66",
            module.resolve_version("0.2.10-dev.66", ["0.2.10-dev.65"]),
        )

    def test_advances_past_higher_published_sequence(self):
        self.assertEqual(
            "0.2.10-dev.66",
            module.resolve_version(
                "0.2.10-dev.53",
                ["0.2.10-dev.52", "0.2.10-dev.65", "0.2.11-dev.9"],
            ),
        )

    def test_rejects_non_numeric_development_version(self):
        with self.assertRaisesRegex(ValueError, "numeric 0.x development version"):
            module.resolve_version("0.2.10-preview", ["0.2.10-dev.65"])

    def test_project_version_restores_before_minver(self):
        captured = None

        def runner(arguments, **options):
            nonlocal captured
            captured = (arguments, options)
            return subprocess.CompletedProcess(
                arguments,
                0,
                stdout="0.2.10-dev.53\n",
            )

        self.assertEqual(
            "0.2.10-dev.53",
            module.project_version(Path("src/libs/WebRTC/WebRTC.csproj"), runner),
        )
        self.assertIn("-restore", captured[0])
        self.assertIn("-t:MinVer", captured[0])
        self.assertTrue(captured[1]["check"])


if __name__ == "__main__":
    unittest.main()
