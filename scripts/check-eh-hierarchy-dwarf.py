#!/usr/bin/env python3
"""Actual LLVM verifier positive/negative controls for the candidate's tree shape."""
import argparse
import json
from pathlib import Path
import subprocess

p=argparse.ArgumentParser(description=__doc__);p.add_argument('tree');p.add_argument('output',type=Path);args=p.parse_args()
data=json.loads(Path(args.tree).read_text());args.output.mkdir(parents=True,exist_ok=False)

def emit(nodes,nested):
    result=[]
    for node in nodes:
        result += [f'.uleb128 {3 if node["try"] else 4}',f'.quad _fixture+{node["offset"]}',f'.long {node["length"]}']
        if nested: result += emit(node['children'],True)+['.byte 0']
        else: result += ['.byte 0']+emit(node['children'],False)
    return result

header='''.text
.globl _fixture
_fixture:
  ret
  .space 60
.section __DWARF,__debug_abbrev,regular,debug
'''
# CU/subprogram/try/catch, all with child lists and low/high PC. Their ranges
# differ only by hierarchy in the two objects, never by truncation/removal.
for code,tag in ((1,0x11),(2,0x2e),(3,0x32),(4,0x25)):
    header+=f'.uleb128 {code}\n.uleb128 {tag}\n.byte 1\n.uleb128 0x11\n.uleb128 1\n.uleb128 0x12\n.uleb128 6\n.byte 0,0\n'
header+='''.byte 0
.section __DWARF,__debug_info,regular,debug
.long Lend-Lstart
Lstart:
.short 4
.long 0
.byte 8
.uleb128 1
.quad _fixture
.long 64
.uleb128 2
.quad _fixture
.long 64
'''

def original(nodes):
    result={}
    for node in nodes:
        key=(node['originOffset'],node['originLength'],node['try'])
        result[key]=dict(offset=key[0],length=key[1],try_kind=key[2])
        for child in original(node['children']):
            result[(child['offset'],child['length'],child['try'])]=dict(offset=child['offset'],length=child['length'],try_kind=child['try'])
    return [dict(offset=r['offset'],length=r['length'],**{'try':r['try_kind']},children=[]) for r in result.values()]

proof={'cases':[]}
for index,case in enumerate(data['cases']):
    current={}
    for mode in ('flat','nested'):
        assembly=args.output/(str(index)+'-'+mode+'.s');obj=args.output/(str(index)+'-'+mode+'.o')
        nodes=case['roots'] if mode=='nested' else original(case['roots'])
        assembly.write_text(header+'\n'.join(emit(nodes,mode=='nested'))+'\n.byte 0,0\nLend:\n')
        subprocess.run(['xcrun','clang','-c',str(assembly),'-o',str(obj),'-Werror'],check=True,timeout=30)
        result=subprocess.run(['xcrun','dwarfdump','--verify',str(obj)],capture_output=True,text=True,timeout=30)
        (args.output/(str(index)+'-'+mode+'.log')).write_text(result.stdout+result.stderr)
        print(result.stdout,end='');print(result.stderr,end='')
        current[mode+'_exit_code']=result.returncode
    if current['flat_exit_code']==0 or current['nested_exit_code']!=0: raise ValueError('EH hierarchy control did not isolate overlap gate')
    proof['cases'].append(current)
proof['scope']='native scope coverage preserved across partitions; not source lexical nesting or general compiler acceptance'
(args.output/'proof.json').write_text(json.dumps(proof,indent=2)+'\n')
