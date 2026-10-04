// Authored test-only signal. Codecs are public APIs of the pinned browser tool.
export async function createAudioProbe(peer) {
  const context=new AudioContext({sampleRate:48000,sinkId:{type:'none'}});
  await context.resume();
  if(context.state!=='running' || context.sampleRate!==48000)throw new Error('Audio context did not start at 48kHz');
  const measure=(samples,f1,f2)=>{
    let energy=0,a=0,b=0,c=0,d=0;
    for(let i=0;i<samples.length;i++){
      const x=samples[i];energy+=x*x;
      a+=x*Math.cos(2*Math.PI*f1*i/48000);b+=x*Math.sin(2*Math.PI*f1*i/48000);
      c+=x*Math.cos(2*Math.PI*f2*i/48000);d+=x*Math.sin(2*Math.PI*f2*i/48000);
    }
    const n=samples.length;return {rms:Math.sqrt(energy/n),a:(a*a+b*b)/(n*n),b:(c*c+d*d)/(n*n),samples:n};
  };
  const validate=(windows,label)=>{
    const first=windows.map((w,i)=>({w,i})).filter(({w})=>w.rms>.07 && w.rms<.4 && w.a>.003 && w.a>w.b*6);
    const second=windows.map((w,i)=>({w,i})).filter(({w})=>w.rms>.07 && w.rms<.4 && w.b>.003 && w.b>w.a*6);
    const tail=windows.filter((w,i)=>i>(second.at(-1)?.i??Infinity) && w.rms<.025);
    if(first.length<25 || second.length<25 || tail.length<4 || first.at(-1).i>=second[0].i)throw new Error(label+' missing ordered tones/final silence: '+JSON.stringify({first:first.length,second:second.length,tail:tail.length,windows:windows.length}));
    return {firstToneWindows:first.length,secondToneWindows:second.length,finalSilentWindows:tail.length,decodedSamples:windows.reduce((sum,w)=>sum+w.samples,0)};
  };
  const source=context.createMediaStreamDestination();
  const oscillator=context.createOscillator();const gain=context.createGain();gain.gain.value=0;
  oscillator.connect(gain).connect(source);oscillator.start();
  peer.addTrack(source.stream.getAudioTracks()[0],source.stream);
  const windows=[];let observer,remoteSource,observerFailure,playback;
  const module=URL.createObjectURL(new Blob([`
    const measure=${measure.toString()};
    class OwnedSignalObserver extends AudioWorkletProcessor {
      constructor(){super();this.samples=new Float32Array(960);this.at=0;this.count=0;}
      process(inputs,outputs){
        const input=inputs[0]?.[0];
        if(input)for(const value of input){
          this.samples[this.at++]=value;
          if(this.at===960){
            if(++this.count>4096){this.port.postMessage({error:'Audio observation bound exceeded'});return false;}
            this.port.postMessage(measure(this.samples,997,1801));this.at=0;
          }
        }
        return true;
      }
    }
    registerProcessor('owned-signal-observer',OwnedSignalObserver);
  `],{type:'text/javascript'}));
  try{await context.audioWorklet.addModule(module);}finally{URL.revokeObjectURL(module);}
  peer.addEventListener('track',e=>{
    if(e.track.kind!=='audio')return;
    if(observer){observerFailure='More than one remote audio track';return;}
    observer=new AudioWorkletNode(context,'owned-signal-observer');
    observer.port.onmessage=e=>{if(e.data.error)observerFailure=e.data.error;else if(windows.length>=4096)observerFailure='Audio observation bound exceeded';else windows.push(e.data);};
    observer.onprocessorerror=()=>observerFailure='Audio worklet failed';
    const stream=new MediaStream([e.track]);
    playback=document.createElement('audio');playback.volume=0;playback.srcObject=stream;playback.play().catch(e=>observerFailure=String(e));
    remoteSource=context.createMediaStreamSource(stream);remoteSource.connect(observer).connect(context.destination);
  });
  const pause=ms=>new Promise(resolve=>setTimeout(resolve,ms));
  const check=async(url,options)=>{const response=await fetch('http://127.0.0.1:9430'+url,options);if(!response.ok)throw new Error('Owned audio request failed '+url+': '+await response.text());return response;};
  return {
    async run(){
      if(!observer)throw new Error('No negotiated remote audio track');
      // Independently reject silent/wrong-frequency measurement input before observing transport.
      for(const frequency of [0,2500]){
        const wrong=Array.from({length:30},()=>measure(Float32Array.from({length:960},(_,i)=>.25*Math.sin(2*Math.PI*frequency*i/48000)),997,1801));
        let rejected=false;try{validate(wrong,'negative control');}catch{rejected=true;}if(!rejected)throw new Error('Audio assertion admitted the wrong signal');
      }
      await check('/audio-capture-start');
      oscillator.frequency.value=733;gain.gain.value=.25;await pause(700);
      oscillator.frequency.value=1331;await pause(700);
      gain.gain.value=0;await pause(400);
      const incoming=(await (await check('/audio-incoming')).text()).split('\n').filter(Boolean).map(line=>{
        const [source,sequence,timestamp,marker,payload]=line.split(',');return {source:Number(source),sequence:Number(sequence),timestamp:Number(timestamp),marker:Number(marker),data:Uint8Array.from(atob(payload),c=>c.charCodeAt(0))};
      });
      if(incoming.length<40 || incoming.length>128 || incoming.some(p=>p.source===0 || p.source!==incoming[0].source || p.data.length<1 || p.data.length>1275 || ![0,1].includes(p.marker)))throw new Error('Invalid authenticated Opus capture: '+JSON.stringify({packets:incoming.length,sources:[...new Set(incoming.map(p=>p.source))],sizes:incoming.map(p=>p.data.length)}));
      for(let i=1;i<incoming.length;i++){
        const delta=(incoming[i].timestamp-incoming[i-1].timestamp)>>>0;
        if(((incoming[i].sequence-incoming[i-1].sequence)&65535)!==1 || delta===0 || delta>4800)throw new Error('Captured Opus sequence/timestamp gap');
      }
      const decoded=[];let decodeError;
      const decoder=new AudioDecoder({error:e=>decodeError=String(e),output:data=>{
        try{
          if(data.sampleRate!==48000 || data.numberOfChannels<1 || data.numberOfFrames<1 || data.numberOfFrames>5760)throw new Error('Unexpected Opus audio format');
          const pcm=new Float32Array(data.numberOfFrames);data.copyTo(pcm,{planeIndex:0,format:'f32-planar'});decoded.push(measure(pcm,733,1331));
        }catch(e){decodeError=String(e);}finally{data.close();}
      }});
      decoder.configure({codec:'opus',sampleRate:48000,numberOfChannels:2});
      for(const packet of incoming)decoder.decode(new EncodedAudioChunk({type:'key',timestamp:Math.round(((packet.timestamp-incoming[0].timestamp)>>>0)*1000000/48000),data:packet.data}));
      await decoder.flush();decoder.close();if(decodeError)throw new Error(decodeError);
      const receiveProof=validate(decoded,'Browser to owned peer');
      const chunks=[];let encodeError;
      const encoder=new AudioEncoder({error:e=>encodeError=String(e),output:chunk=>{const data=new Uint8Array(chunk.byteLength);chunk.copyTo(data);chunks.push({timestamp:chunk.timestamp,duration:chunk.duration,type:chunk.type,data});}});
      encoder.configure({codec:'opus',sampleRate:48000,numberOfChannels:1,bitrate:32000,opus:{format:'opus',frameDuration:20000,usedtx:false}});
      for(let frame=0;frame<90;frame++){
        const frequency=frame<35?997:frame<70?1801:0;
        const pcm=Float32Array.from({length:960},(_,i)=>.25*Math.sin(2*Math.PI*frequency*(frame*960+i)/48000));
        const data=new AudioData({format:'f32',sampleRate:48000,numberOfChannels:1,numberOfFrames:960,timestamp:frame*20000,data:pcm});encoder.encode(data);data.close();
      }
      await encoder.flush();encoder.close();if(encodeError)throw new Error(encodeError);
      if(chunks.length<90 || chunks.length>92 || chunks.some(c=>c.type!=='key' || c.duration!==20000 || c.data.length<1 || c.data.length>1275))throw new Error('Encoder did not emit bounded 20ms raw Opus');
      const audioStats=async()=>{
        const stats=[];(await peer.getStats()).forEach(s=>{if(s.type==='inbound-rtp' && s.kind==='audio')stats.push({packetsReceived:s.packetsReceived,packetsLost:s.packetsLost,totalSamplesReceived:s.totalSamplesReceived,jitterBufferEmittedCount:s.jitterBufferEmittedCount,concealedSamples:s.concealedSamples,jitterBufferDelay:s.jitterBufferDelay,insertedSamplesForDeceleration:s.insertedSamplesForDeceleration,removedSamplesForAcceleration:s.removedSamplesForAcceleration});});
        if(stats.length>1)throw new Error('Unexpected multiple audio sources');return stats[0];
      };
      const runs=[];let streamStart,nextTimestampBase=100000;
      for(const loss of [false,true]){
        const prior=await audioStats();
        await check('/audio-observation-start?loss='+loss+'&startup-delay='+!loss);
        windows.length=0;
        const requestedAt=performance.now();let started,activationRoundTripMs;
        const timestampBase=Math.max(nextTimestampBase,streamStart===undefined?100000:100000+Math.round((requestedAt-streamStart)/20)*960);
        nextTimestampBase=timestampBase+chunks.length*960;
        for(let i=0;i<chunks.length;i++){
          const chunk=chunks[i];
          // Integer microseconds map to the nearest 48 kHz sample; sends use an absolute monotonic deadline.
          const clock=Math.round((chunk.timestamp-chunks[0].timestamp)*48/1000);
          if(clock!==i*960)throw new Error('Opus encoder clock changed');
          if(i>0){const delay=started+i*20-performance.now();if(delay>0)await pause(delay);}
          await check('/audio?timestamp='+((timestampBase+clock)>>>0)+'&marker='+(i===0),{method:'POST',body:chunk.data});
          if(i===0){started=performance.now();activationRoundTripMs=started-requestedAt;streamStart??=started;}
        }
        if(!loss && activationRoundTripMs<110)throw new Error('Startup delay fault was not observed');
        const expectedLoss=loss?3:0;let stats;
        for(let n=0;n<100;n++){
          if(observerFailure)throw new Error(observerFailure);
          stats=await audioStats();
          if(stats && stats.packetsReceived-(prior?.packetsReceived??0)===chunks.length-expectedLoss){
            try{validate(windows,'Owned peer to browser');break;}catch{}
          }
          await pause(25);
        }
        const sendProof=validate(windows,'Owned peer to browser');
        if(!stats || ['packetsReceived','packetsLost','totalSamplesReceived','jitterBufferEmittedCount','concealedSamples'].some(k=>!Number.isFinite(stats[k]) || stats[k]<0) || stats.packetsReceived-(prior?.packetsReceived??0)!==chunks.length-expectedLoss || stats.packetsLost-(prior?.packetsLost??0)!==expectedLoss || stats.totalSamplesReceived-(prior?.totalSamplesReceived??0)<48000)throw new Error('Browser audio RTP evidence incomplete: '+JSON.stringify({prior,stats,expectedLoss}));
        const wire=(await (await check('/audio-observation')).text()).split('\n').map(line=>{const [sequence,timestamp,at,dropped]=line.split(',').map(Number);return {sequence,timestamp,at,dropped};});
        if(wire.length!==chunks.length || wire.some((p,i)=>!Number.isFinite(p.at) || p.at<0 || (i>0 && p.at<wire[i-1].at) || ((p.sequence-wire[0].sequence)&65535)!==i || ((p.timestamp-wire[0].timestamp)>>>0)!==i*960 || p.dropped!==(loss && [20,45,46].includes(i)?1:0)))throw new Error('Protected audio clock/loss evidence mismatch');
        const drift=wire.map((p,i)=>Math.abs(p.at-i*20));const ordered=[...drift].sort((a,b)=>a-b);
        const maximumClockDriftMs=Math.max(...drift),p95ClockDriftMs=ordered[Math.ceil(ordered.length*.95)-1];
        if(maximumClockDriftMs>80 || p95ClockDriftMs>40)throw new Error('Protected RTP pacing missed its budget: '+JSON.stringify({maximumClockDriftMs,p95ClockDriftMs,activationRoundTripMs,worst:wire.map((p,i)=>({i,at:p.at,drift:p.at-i*20})).sort((a,b)=>Math.abs(b.drift)-Math.abs(a.drift)).slice(0,8)}));
        // Require ongoing non-silent output inside each tone, then distinct second tone and silence.
        const active=windows.map((w,i)=>({w,i})).filter(({w})=>w.rms>.07 && (w.a>.003 || w.b>.003));
        let quiet=0,maximumSilentWindows=0;
        for(let i=active[0].i;i<=active.at(-1).i;i++){quiet=windows[i].rms<.025?quiet+1:0;maximumSilentWindows=Math.max(maximumSilentWindows,quiet);}
        if(maximumSilentWindows>(loss?3:1))throw new Error('Decoded audio stalled inside the authored tones: '+maximumSilentWindows+' windows');
        runs.push({forcedLoss:expectedLoss,protectedPackets:wire.length,droppedSequences:wire.filter(p=>p.dropped).map(p=>p.sequence),pacing:{activationRoundTripMs,maximumClockDriftMs,p95ClockDriftMs,lastPacketAtMs:wire.at(-1).at},sendProof,maximumSilentWindows,stats,concealedSampleDelta:stats.concealedSamples-(prior?.concealedSamples??0)});
      }
      return {sampleRate:48000,receivedPackets:incoming.length,sentPacketsPerRun:chunks.length,receiveProof,runs};
    },
    async close(){oscillator.stop();source.stream.getTracks().forEach(t=>t.stop());playback?.pause();if(playback)playback.srcObject=null;remoteSource?.disconnect();observer?.disconnect();await context.close();}
  };
}
