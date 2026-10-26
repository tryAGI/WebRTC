#!/usr/bin/env python3
"""Real LLDB launch/frame/local checks for an authored NativeAOT EH fixture."""
import argparse
import json
import os
from pathlib import Path
import signal
import subprocess


def run_debugger(debugger):
    import lldb
    output=Path(os.environ['TRYAGI_EH_DEBUG_PROOF']);proof=dict(success=False,stops=[],scope='authored native EH source/frame/local fixture; not universal lexical correctness')
    process=None
    try:
        debugger.SetAsync(False)
        target=debugger.CreateTarget(os.environ['TRYAGI_EH_DEBUG_TARGET'])
        if not target.IsValid():raise ValueError('LLDB could not create the native target')
        bp=target.BreakpointCreateByRegex('EhDebugProbe.*Checkpoint')
        if bp.GetNumLocations()!=1:raise ValueError('Checkpoint must resolve to exactly one native location')
        checkpoint=os.environ.get('TRYAGI_EH_DEBUG_CHECKPOINT','entry')
        if checkpoint=='entry':
            # Check arguments before optimized code can reuse their registers.
            address=bp.GetLocationAtIndex(0).GetAddress().GetFunction().GetStartAddress()
            if not address.IsValid():raise ValueError('Checkpoint entry address unavailable')
            bp.SetEnabled(False)
            entry=target.BreakpointCreateBySBAddress(address)
            if entry.GetNumLocations()!=1:raise ValueError('Entry breakpoint did not resolve')
        elif checkpoint!='source':raise ValueError('Unknown checkpoint mode')
        proof['checkpoint']=checkpoint
        launch=lldb.SBLaunchInfo([]);launch.SetWorkingDirectory(str(Path(os.environ['TRYAGI_EH_DEBUG_TARGET']).parent))
        error=lldb.SBError();process=target.Launch(launch,error)
        if error.Fail():raise ValueError('LLDB launch failed: '+str(error.GetCString()))
        expected=((1,111,101,'tryOnly','DEBUG_TRY'),(3,131,101,'finallyOnly','DEBUG_FINALLY'),
                  (1,110,100,'tryOnly','DEBUG_TRY'),(2,120,100,'catchOnly','DEBUG_CATCH'),(3,130,100,'finallyOnly','DEBUG_FINALLY'))
        source=Path(os.environ['TRYAGI_EH_DEBUG_SOURCE']).read_text().splitlines()
        def integer(frame,name):
            value=frame.FindVariable(name);error=lldb.SBError()
            result=value.GetValueAsSigned(error,-999999)
            if not value.IsValid() or error.Fail():
                proof['unavailable_variable']=dict(name=name,function=frame.GetFunctionName(),line=frame.GetLineEntry().GetLine())
                raise ValueError('Unavailable debugger variable: '+name)
            return result
        for stage,value,outer,local,marker in expected:
            if process.GetState()!=lldb.eStateStopped:
                proof['process_state']=process.GetState();proof['exit_status']=process.GetExitStatus();proof['exit_description']=process.GetExitDescription()
                raise ValueError('Fixture did not stop at each expected EH checkpoint')
            threads=[t for t in process if t.GetStopReason()==lldb.eStopReasonBreakpoint]
            if len(threads)!=1:raise ValueError('Ambiguous or missing checkpoint thread')
            top=threads[0].GetFrameAtIndex(0);parent=threads[0].GetFrameAtIndex(1)
            entry=parent.GetLineEntry();line=entry.GetLine();marker_line=next(i+1 for i,s in enumerate(source) if marker in s)
            item=dict(stage=integer(top,'stage'),value=integer(top,'value'),outer=integer(parent,'outer'),
                      local=local,local_value=integer(parent,local),function=parent.GetFunctionName(),
                      source_file=entry.GetFileSpec().GetFilename(),line=line,marker_line=marker_line)
            instructions=top.GetFunction().GetInstructions(target)
            item['checkpoint_offset']=top.GetPC()-top.GetFunction().GetStartAddress().GetLoadAddress(target)
            item['instructions']=[dict(offset=instructions.GetInstructionAtIndex(i).GetAddress().GetLoadAddress(target)-top.GetFunction().GetStartAddress().GetLoadAddress(target),
                                       mnemonic=instructions.GetInstructionAtIndex(i).GetMnemonic(target),operands=instructions.GetInstructionAtIndex(i).GetOperands(target)) for i in range(min(16,instructions.GetSize()))]
            proof['stops'].append(item)
            if (item['stage'],item['value'],item['outer'],item['local_value'])!=(stage,value,outer,value):raise ValueError('Debugger values differ from independent expected EH state')
            if not item['function'] or 'EhDebugProbe' not in item['function'] or 'Probe' not in item['function']:raise ValueError('Wrong caller frame')
            if item['source_file']!='Program.cs' or not marker_line<=line<=marker_line+2:raise ValueError('Wrong source mapping for EH phase')
            process.Continue()
        if process.GetState()!=lldb.eStateExited or process.GetExitStatus()!=0:raise ValueError('Unexpected extra checkpoint, native failure or incomplete execution')
        proof['success']=True
    except Exception as error:
        proof['error']=str(error)
    finally:
        if process is not None and process.IsValid() and process.GetState()!=lldb.eStateExited:process.Kill()
        output.write_text(json.dumps(proof,indent=2)+'\n')


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('binary',type=Path);p.add_argument('source',type=Path);p.add_argument('output',type=Path);p.add_argument('--checkpoint',choices=('entry','source'),default='entry');args=p.parse_args()
    args.output.parent.mkdir(parents=True,exist_ok=True)
    if args.output.exists():raise ValueError('Preserve an existing debugger result')
    env=dict(os.environ,TRYAGI_EH_DEBUG_TARGET=str(args.binary.resolve()),TRYAGI_EH_DEBUG_SOURCE=str(args.source.resolve()),TRYAGI_EH_DEBUG_PROOF=str(args.output.resolve()),TRYAGI_EH_DEBUG_CHECKPOINT=args.checkpoint)
    command="import runpy; runpy.run_path("+repr(str(Path(__file__).resolve()))+",run_name='lldb_fixture')['run_debugger'](lldb.debugger)"
    with (args.output.parent/'lldb.log').open('w') as log:
        child=subprocess.Popen(['xcrun','lldb','--batch','--no-lldbinit','-o','script '+command],env=env,stdout=log,stderr=subprocess.STDOUT,start_new_session=True)
        try: code=child.wait(timeout=120)
        except subprocess.TimeoutExpired:
            os.killpg(child.pid,signal.SIGKILL);child.wait();raise
    if code or not args.output.exists() or not json.loads(args.output.read_text())['success']:raise SystemExit('LLDB semantic fixture failed; retain proof and complete log')
