import { spawn } from 'node:child_process';
import { createServer } from 'node:http';
import { access, mkdtemp, rm } from 'node:fs/promises';
const mode=process.argv[2];
if(!['answer-active','answer-passive','offer'].includes(mode)) throw new Error('Unknown browser test mode');
const server = createServer((req, res) => { res.setHeader('Content-Type', 'text/html'); res.end('<!doctype html><title>Owned local WebRTC probe</title>'); });
await new Promise(resolve => server.listen(9235, '127.0.0.1', resolve));
const profile=await mkdtemp('/tmp/owned-browser-profile-');
// The immutable image uses different distribution directory names on arm64/x64.
let executable;
for(const path of ['/ms-playwright/chromium-1234/chrome-linux/chrome','/ms-playwright/chromium-1234/chrome-linux64/chrome']) {
  try { await access(path);executable=path;break; } catch {}
}
if(!executable)throw new Error('Pinned Chromium binary missing');
const chrome = spawn(executable, [
  '--headless', '--no-sandbox', '--disable-gpu', '--disable-background-networking',
  '--disable-component-update', '--disable-sync', '--no-first-run', '--no-default-browser-check',
  '--allow-loopback-in-peer-connection', '--disable-features=WebRtcHideLocalIpsWithMdns',
  '--remote-debugging-address=127.0.0.1', '--remote-debugging-port=9222', '--user-data-dir='+profile, 'about:blank'
], { stdio: ['ignore','ignore','pipe'] });
let startupError='';
chrome.stderr.on('data',chunk=>startupError=(startupError+chunk.toString()).slice(-4096));
let spawnFailure;chrome.on('error',error=>spawnFailure=error);
let socket;
const deadline = AbortSignal.timeout(45000);
try {
  for(let i=0;i<100;i++) {
    try { const response=await fetch('http://127.0.0.1:9430/state',{signal:deadline});if(response.ok)break; } catch {}
    await new Promise(resolve=>setTimeout(resolve,50));
  }
  for (let i = 0; i < 100; i++) {
    if(spawnFailure || chrome.exitCode!==null || chrome.signalCode!==null) throw new Error('Chromium startup failed: '+(spawnFailure || chrome.exitCode || chrome.signalCode)+' '+startupError);
    try { const response = await fetch('http://127.0.0.1:9222/json/version', { signal: deadline }); if (response.ok) break; } catch { }
    await new Promise(resolve => setTimeout(resolve, 50));
  }
  const response = await fetch('http://127.0.0.1:9222/json/new?http://127.0.0.1:9235/', { method: 'PUT', signal: deadline });
  if (!response.ok) throw new Error(`CDP target status ${response.status}`);
  const target = await response.json();
  socket = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => { socket.addEventListener('open', resolve, { once: true }); socket.addEventListener('error', reject, { once: true }); deadline.addEventListener('abort', () => reject(deadline.reason), { once: true }); });
  let id = 0; const pending = new Map();
  socket.addEventListener('message', event => { const m = JSON.parse(event.data); if (m.id && pending.has(m.id)) { const p = pending.get(m.id); pending.delete(m.id); m.error ? p.reject(new Error(JSON.stringify(m.error))) : p.resolve(m.result); } });
  const send = (method, params = {}) => new Promise((resolve, reject) => { const key = ++id; pending.set(key, { resolve, reject }); socket.send(JSON.stringify({ id: key, method, params })); deadline.addEventListener('abort', () => reject(deadline.reason), { once: true }); });
  await send('Runtime.enable');
  const result = await send('Runtime.evaluate', { awaitPromise: true, returnByValue: true, expression: `(async () => {
    const mode=${JSON.stringify(mode)};
    const peer = new RTCPeerConnection({ iceServers: [] });
    const video=document.createElement('video');video.autoplay=true;video.muted=true;video.playsInline=true;document.body.appendChild(video);
    const output=new OffscreenCanvas(320,240); const pixels=output.getContext('2d'); const frames=[];
    const observe=(now,meta)=>{ pixels.drawImage(video,0,0); const sample=x=>Array.from(pixels.getImageData(x,40,1,1).data).slice(0,3);frames.push({presented:meta.presentedFrames,width:meta.width,height:meta.height,old:sample(24),fresh:sample(72),last:sample(112),background:sample(0)});video.requestVideoFrameCallback(observe); };
    peer.ontrack=e=>{ if(e.track.kind==='video') { video.srcObject=new MediaStream([e.track]);video.play();video.requestVideoFrameCallback(observe); } };
    const channel = peer.createDataChannel('owned'); let reply = '';
    channel.onmessage = e => reply = e.data;
    channel.onopen = () => channel.send('browser-ready');
    let candidates=0;
    if(mode==='offer') {
      const remote=await fetch('http://127.0.0.1:9430/offer');if(!remote.ok)throw new Error('Owned offer failed');
      await peer.setRemoteDescription({type:'offer',sdp:await remote.text()});
      await peer.setLocalDescription(await peer.createAnswer());
    } else {
      peer.addTransceiver('audio',{direction:'recvonly'});peer.addTransceiver('video',{direction:'recvonly'});
      await peer.setLocalDescription(await peer.createOffer());
    }
    if(peer.iceGatheringState!=='complete')await new Promise(resolve=>{peer.onicegatheringstatechange=()=>{if(peer.iceGatheringState==='complete')resolve();};setTimeout(resolve,2500);});
    candidates=peer.localDescription.sdp.split('\\r\\n').filter(s=>s.startsWith('a=candidate:')).length;
    const response=await fetch('http://127.0.0.1:9430/answer',{method:'POST',body:peer.localDescription.sdp});
    if(!response.ok)throw new Error('Owned SDP status '+response.status+' '+await response.text());
    if(mode!=='offer')await peer.setRemoteDescription({type:'answer',sdp:await response.text()});
    for (let n=0;n<400 && reply !== 'owned-ready';n++) { const state = await fetch('http://127.0.0.1:9430/state'); if (!state.ok) throw new Error('Owned connection failed: '+await state.text()); await new Promise(r => setTimeout(r,50)); }
    if (reply !== 'owned-ready' || peer.connectionState !== 'connected') throw new Error('Connection did not exchange data: '+peer.connectionState+'/'+peer.iceConnectionState);
    const paintCanvas=new OffscreenCanvas(320,240);const paint=paintCanvas.getContext('2d'); const chunks=[];let encodeError;
    const encoder=new VideoEncoder({ error:e=>encodeError=String(e),output:c=>{const data=new Uint8Array(c.byteLength);c.copyTo(data);chunks.push({type:c.type,data});} });
    encoder.configure({codec:'vp8',width:320,height:240,bitrate:200000,framerate:10,latencyMode:'realtime'});
    const sendFrame=async(i,key)=>{paint.fillStyle='rgb(128,128,128)';paint.fillRect(0,0,320,240);paint.fillStyle='black';paint.fillRect(16+i*8,32,16,16);const f=new VideoFrame(paintCanvas,{timestamp:i*100000});encoder.encode(f,{keyFrame:key});f.close();await encoder.flush();if(encodeError)throw new Error(encodeError);const chunk=chunks.shift();if(!chunk || chunk.type!==(key?'key':'delta'))throw new Error('Encoder did not emit requested frame type');const data=chunk.data;const r=await fetch('http://127.0.0.1:9430/frame?timestamp='+i*9000,{method:'POST',body:data});if(!r.ok)throw new Error('Owned frame send failed: '+await r.text());};
    const wait=async(test,what)=>{for(let n=0;n<200 && !test();n++)await new Promise(r=>setTimeout(r,50));if(!test())throw new Error(what+' frames='+JSON.stringify(frames));};
    await sendFrame(0,true);await wait(()=>frames.some(f=>f.width===320 && f.height===240 && f.old.every(x=>x<10) && f.fresh.every(x=>x>110 && x<150)),'Initial frame was not decoded');
    let priorPli=0;(await peer.getStats()).forEach(s=>{if(s.type==='inbound-rtp' && s.kind==='video')priorPli=s.pliCount;});
    const armed=await fetch('http://127.0.0.1:9430/loss');if(!armed.ok)throw new Error('Loss was not armed');await sendFrame(1,false);
    for(let i=2;i<6;i++){await sendFrame(i,false);await new Promise(r=>setTimeout(r,100));}
    let feedback;
    for(let n=0;n<200;n++){feedback=(await (await fetch('http://127.0.0.1:9430/feedback')).text()).split(',').map(Number);if(feedback[0]>0 && feedback[1]===1)break;await new Promise(r=>setTimeout(r,50));}
    if(feedback[0]<1 || feedback[1]!==1)throw new Error('No admitted PLI after real packet loss: '+feedback);
    await sendFrame(6,true);await wait(()=>frames.some(f=>f.fresh.every(x=>x<10) && f.old.every(x=>x>110 && x<150)),'Fresh recovery frame was not decoded');
    for(let i=7;i<12;i++){await sendFrame(i,false);await new Promise(r=>setTimeout(r,100));}
    await wait(()=>frames.some(f=>f.last.every(x=>x<10) && f.fresh.every(x=>x>110 && x<150)), 'Post-recovery delta progress absent');
    encoder.close();
    const stats=[];(await peer.getStats()).forEach(s=>{if(s.type==='transport' || s.type==='inbound-rtp' && s.kind==='video')stats.push({type:s.type,dtlsState:s.dtlsState,framesDecoded:s.framesDecoded,keyFramesDecoded:s.keyFramesDecoded,pliCount:s.pliCount,packetsLost:s.packetsLost,framesReceived:s.framesReceived,framesDropped:s.framesDropped,nackCount:s.nackCount});});
    await fetch('http://127.0.0.1:9430/close');peer.close();
    if(!isSecureContext)throw new Error('Browser origin is not secure');
    const media=stats.find(s=>s.type==='inbound-rtp');if(!media || media.framesDecoded<3 || media.keyFramesDecoded<2 || media.pliCount<=priorPli || media.packetsLost!==1)throw new Error('Browser decode/recovery stats incomplete: '+JSON.stringify(stats));
    return {mode,priorPli,secure:isSecureContext,candidates,reply,feedback,frames,stats};
  })()` });
  if (result.exceptionDetails) throw new Error(JSON.stringify(result.exceptionDetails));
  console.log(JSON.stringify(result.result.value, null, 2));
} finally {
  socket?.close();
  const stopped=chrome.exitCode===null && chrome.signalCode===null && !spawnFailure ? new Promise(resolve=>chrome.once('exit',resolve)) : Promise.resolve();
  chrome.kill('SIGTERM');
  const force=setTimeout(()=>chrome.kill('SIGKILL'),2000);
  await stopped;clearTimeout(force);
  await new Promise(resolve=>server.close(resolve));
  await rm(profile,{recursive:true,force:true});
}
