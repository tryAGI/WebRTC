#!/usr/bin/env python3
"""Relocate one verified debug archive through LLVM's supported YAML debug map."""
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys


def digest(path): return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def relocate(text, original, replacement):
    # Actual LLVM debug-map filenames are restricted here to the known public
    # archive prefix and parenthesized member name. Never rewrite symbol records.
    changed = 0; swift = 0; result = []
    for line in text.splitlines(keepends=True):
        if re.match(r'^\s+(?:-\s+)?filename:', line) and original + '(' in line:
            if line.count(original + '(') != 1: raise ValueError('Ambiguous archive debug-map entry')
            swift += int(original + '(pal_swiftbindings.o)' in line)
            line = line.replace(original + '(', replacement + '(', 1); changed += 1
        result.append(line)
    if not changed or swift != 1: raise ValueError('Original Swift archive debug-map entry not uniquely identified')
    return ''.join(result), changed


def main(arguments):
    # MSBuild probes tool identity before invoking the actual linker. Delegate
    # exactly --version, preserving its complete output/status; no other option
    # bypasses archive identity checks or full verification.
    if arguments == ['--version']:
        data = json.loads(Path(os.environ['TRYAGI_NATIVE_DEBUG_INPUT']).read_text())
        return subprocess.run([data['real_dsymutil'], '--version'], timeout=30).returncode
    if len(arguments) != 1 or arguments[0].startswith('-'): raise ValueError('Expected one owned NativeBinary input, no diagnostic-disabling options')
    binary = Path(arguments[0]).resolve(); workspace = Path(os.environ['GITHUB_WORKSPACE']).resolve()
    if not binary.is_file() or not binary.is_relative_to(workspace): raise ValueError('NativeBinary is outside the owned job workspace')
    manifest_path = Path(os.environ['TRYAGI_NATIVE_DEBUG_INPUT']).resolve(); data = json.loads(manifest_path.read_text())
    original = Path(data['official_archive']); replacement = Path(data['debug_archive'])
    if digest(original) != data['official_sha256'] or digest(replacement) != data['debug_sha256']: raise ValueError('Verified archive identity changed')
    proof = json.loads((manifest_path.parent / 'proof.json').read_text())
    if proof.get('code_data_layout_relocations_symbols_equal') is not True or proof.get('real_code_mutation_rejected') is not True: raise ValueError('Required nondebug equivalence/control proof missing')
    real_tool = data['real_dsymutil']
    # stdout is structured YAML input, not filtered diagnostics. All stderr is
    # preserved verbatim. A warning/error still fails the wrapper and MSBuild.
    dump = subprocess.run([real_tool, '--dump-debug-map', str(binary)], capture_output=True, text=True, timeout=60)
    sys.stderr.write(dump.stderr)
    diagnostics = [line for line in dump.stdout.splitlines() if re.match(r'^\s*(?:warning|error):', line, re.I)]
    if diagnostics:
        print('\n'.join(diagnostics)); raise RuntimeError('Debug-map extraction emitted diagnostics')
    if dump.returncode or re.search(r'\b(?:warning|error):', dump.stderr, re.I): raise RuntimeError('Debug-map extraction failed or emitted a diagnostic')
    if len(dump.stdout) > 64 * 1024 * 1024: raise ValueError('Debug map exceeds bound')
    mapped, count = relocate(dump.stdout, str(original), str(replacement))
    map_path = manifest_path.parent / 'native-debug-map.yaml'; map_path.write_text(mapped)
    before = digest(binary)
    # No flags remove debug data or silence warnings. --verify validates the
    # linked DWARF; supported -y changes only the source of the input debug map.
    result = subprocess.run([real_tool, '-y', str(map_path), '-o', str(binary) + '.dSYM', '--verify'], timeout=180)
    binary_unchanged = digest(binary) == before
    archive_unchanged = digest(original) == data['official_sha256']
    (manifest_path.parent / 'native-link-proof.json').write_text(json.dumps(dict(native_binary_sha256=before,
        binary_unchanged_by_debug_link=binary_unchanged, official_archive_unchanged=archive_unchanged, relocated_archive_members=count,
        debug_link_exit_code=result.returncode, dwarf_verified=result.returncode == 0,
        debug_map='LLVM supported YAML input; only verified archive filenames relocated', diagnostics_suppressed=False), indent=2) + '\n')
    if not binary_unchanged or not archive_unchanged: raise ValueError('Runtime binary/archive was modified by debug linking')
    return result.returncode


if __name__ == '__main__':
    try: sys.exit(main(sys.argv[1:]))
    except (ValueError, RuntimeError, subprocess.SubprocessError, KeyError) as error:
        print('Verified debug linking failed: ' + str(error), file=sys.stderr); sys.exit(1)
