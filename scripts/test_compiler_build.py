import importlib.util
from pathlib import Path
import unittest
spec=importlib.util.spec_from_file_location('compiler_build',Path(__file__).with_name('check-compiler-build.py'))
build=importlib.util.module_from_spec(spec);spec.loader.exec_module(build)


class CompilerBuildTests(unittest.TestCase):
    def test_native_warnings_fail_even_after_zero_msbuild_summary(self):
        text="ld: warning: ignoring duplicate libraries: 'lib.a'\nwarning: ranlib: empty table\n0 Warning(s)\n0 Error(s)"
        self.assertEqual(len(build.diagnostics(text)),2)

    def test_audit_compiler_cmake_errors_retained(self):
        for text in ('file.cs: error NU1903: vulnerability','file.cs: warning CS8600: contract','CMake Warning at file:1'):
            self.assertEqual(len(build.diagnostics(text)),1)

    def test_filenames_and_summary_are_not_diagnostics(self):
        self.assertEqual(build.diagnostics('Building error.cpp.o\n0 Warning(s)\n0 Error(s)\nBuild succeeded.'),[])


if __name__=='__main__':unittest.main()
