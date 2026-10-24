#!/usr/bin/env python3
"""Patch only a pinned CI source build; never edit installed compiler or linked DWARF."""
import argparse
import hashlib
from pathlib import Path

p=argparse.ArgumentParser(description=__doc__);p.add_argument('runtime',type=Path);args=p.parse_args()
folder=args.runtime/'src/coreclr/tools/aot/ILCompiler.Compiler/Compiler/ObjectWriter/Dwarf'
info=folder/'DwarfInfo.cs';abbrev=folder/'DwarfAbbrev.cs'
if hashlib.sha256(info.read_bytes()).hexdigest()!='34f1a169e73ef3e49b394a9b67bf1ed281ef7d9966c964067126cf5eb0bf2ade': raise ValueError('Unreviewed upstream emitter source')
if hashlib.sha256(abbrev.read_bytes()).hexdigest()!='6ec1e2321754749656be87876eb5b16a778b9546416f4c80e4ad7269ad7da236': raise ValueError('Unreviewed upstream abbreviations')
s=info.read_text();start=s.index('            // EH clauses\n');end=s.index('\n            writer.WriteEndDIE();\n        }',start)
replacement='''            // Preserve shared/nested regions as a containment forest. Unexpected
            // crossing physical ranges fail instead of writing invalid debug data.
            var ranges = new List<EhRangePlanner.Range>();
            foreach (DebugEHClauseInfo clause in _debugEHClauseInfos)
            {
                ranges.Add(new(clause.TryOffset, clause.TryLength, true));
                ranges.Add(new(clause.HandlerOffset, clause.HandlerLength, false));
            }
            var pending = new Stack<(EhRangePlanner.Node Node, bool End)>();
            var roots = EhRangePlanner.Build(checked((uint)_methodSize), ranges);
            for (int i = roots.Count - 1; i >= 0; i--) pending.Push((roots[i], false));
            while (pending.Count > 0)
            {
                var item = pending.Pop();
                if (item.End) { writer.WriteEndDIE(); continue; }
                var region = item.Node.Region;
                writer.WriteStartDIE(region.IsTry ? DwarfAbbrev.TryBlock : DwarfAbbrev.CatchBlock);
                writer.WriteCodeReference(_sectionSymbolName, _methodAddress + region.Offset);
                writer.WriteAddressSize(region.Length);
                pending.Push((item.Node, true));
                for (int i = item.Node.Children.Count - 1; i >= 0; i--) pending.Push((item.Node.Children[i], false));
            }
'''
s=s[:start]+replacement+s[end:]
helper=(Path(__file__).resolve().parent.parent/'tests/diagnostics/DwarfEhPlanner/EhRangePlanner.cs').read_text()
helper=helper.split('// BEGIN EH PLANNER\n',1)[1].split('// END EH PLANNER',1)[0]
pos=s.rfind('\n}');s=s[:pos]+'\n'+helper+s[pos:];info.write_text(s)
s=abbrev.read_text()
for tag in ('try','catch'):
    old='DW_TAG_'+tag+'_block, DW_CHILDREN_no'
    if s.count(old)!=1: raise ValueError('Unexpected upstream EH abbreviation')
    s=s.replace(old,'DW_TAG_'+tag+'_block, DW_CHILDREN_yes')
abbrev.write_text(s)

# Build-tool-only transitive dependency refresh. Keep audit enabled and update
# the vulnerable dependency itself, not its severity or warning policy.
for relative, expected in (
    ('src/tasks/Crossgen2Tasks/Crossgen2Tasks.csproj', '622690e22cc466ef8213ba94aa204c1322d17208fd2f28c44bf72ca7d6739504'),
    ('src/tasks/installer.tasks/installer.tasks.csproj', '698705c333e47b604d8c8e89e34f767998cb2e761a8fe31f6906097461c55338')):
    project = args.runtime / relative
    if hashlib.sha256(project.read_bytes()).hexdigest() != expected: raise ValueError('Unreviewed upstream build task project')
    text = project.read_text()
    reference = '  <ItemGroup><PackageReference Include="System.Security.Cryptography.Pkcs" Version="9.0.21" /></ItemGroup>\n'
    project.write_text(text.replace('</Project>', reference + '</Project>'))
