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
    if(first.length<8 || second.length<8 || tail.length<4 || first.at(-1).i>=second[0].i)throw new Error(label+' missing ordered tones/final silence: '+JSON.stringify({first:first.length,second:second.length,tail:tail.length,windows:windows.length}));
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
      windows.length=0;
      for(let i=0;i<chunks.length;i++){
        const chunk=chunks[i];// WebCodecs exposes integer microseconds; map them to the nearest 48kHz sample.
        const clock=Math.round((chunk.timestamp-chunks[0].timestamp)*48/1000);
        if(clock!==i*960)throw new Error('Opus encoder clock changed');
        await check('/audio?timestamp='+(100000+clock)+'&marker='+(i===0),{method:'POST',body:chunk.data});await pause(20);
      }
      for(let n=0;n<100;n++){if(observerFailure)throw new Error(observerFailure);try{validate(windows,'Owned peer to browser');break;}catch{await pause(25);}}
      let sendProof;try{sendProof=validate(windows,'Owned peer to browser');}catch(e){const stats=[];(await peer.getStats()).forEach(s=>{if(s.type==='inbound-rtp'&&s.kind==='audio')stats.push(s);});throw new Error(e.message+' stats='+JSON.stringify(stats)+' strongest='+JSON.stringify([...windows].sort((a,b)=>b.rms-a.rms).slice(0,5)));}
      const stats=[];(await peer.getStats()).forEach(s=>{if(s.type==='inbound-rtp' && s.kind==='audio')stats.push({packetsReceived:s.packetsReceived,packetsLost:s.packetsLost,totalSamplesReceived:s.totalSamplesReceived,jitterBufferEmittedCount:s.jitterBufferEmittedCount,concealedSamples:s.concealedSamples});});
      if(stats.length!==1 || stats[0].packetsReceived<chunks.length || stats[0].packetsLost!==0 || stats[0].totalSamplesReceived<48000)throw new Error('Browser audio RTP evidence incomplete: '+JSON.stringify(stats));
      return {sampleRate:48000,receivedPackets:incoming.length,sentPackets:chunks.length,receiveProof,sendProof,stats};
    },
    async close(){oscillator.stop();source.stream.getTracks().forEach(t=>t.stop());playback?.pause();if(playback)playback.srcObject=null;remoteSource?.disconnect();observer?.disconnect();await context.close();}
  };
}
