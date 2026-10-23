// Authored synthetic-pixel diagnostics. No frames or encoded payloads are exported.
export function createVideoProof() {
  const video=document.createElement('video');video.autoplay=true;video.muted=true;video.playsInline=true;document.body.appendChild(video);
  const output=new OffscreenCanvas(320,240), pixels=output.getContext('2d');
  const frames=[],presentation=[],source=[],decoded=[],encoded=[],drawable=[],receiver=[];
  let lastMeta,trackReader,trackPump,receiverError;
  const keep=(list,value)=>{if(list.length>=64)list.shift();list.push(value);};
  const sample=(image,center=24)=>{
    pixels.clearRect(0,0,320,240);pixels.drawImage(image,0,0,320,240);
    const point=x=>Array.from(pixels.getImageData(x,40,1,1).data);
    return {old:point(24),fresh:point(72),last:point(112),background:point(0),center:point(center)};
  };
  const fixture=(image,timestamp,stage)=>{
    const content=sample(image,24+timestamp/100000*8);
    if(!content.center.slice(0,3).every(x=>x<10) || !content.background.slice(0,3).every(x=>x>110 && x<150) || content.background[3]!==255)
      throw new Error(stage+' synthetic content mismatch '+JSON.stringify({timestamp,...content}));
    return {timestamp,...content};
  };
  const rgb=content=>({old:content.old.slice(0,3),fresh:content.fresh.slice(0,3),last:content.last.slice(0,3),
    background:content.background.slice(0,3),alpha:content.background[3]});
  const recordDrawable=origin=>{
    // WHATWG HTML: drawImage(video) has no current frame below HAVE_CURRENT_DATA.
    // Keep early callback evidence; use a single lifecycle event, not polling/retries.
    if(!lastMeta || video.readyState<2)return;
    keep(drawable,{presented:lastMeta.presentedFrames,width:lastMeta.width,height:lastMeta.height,
      mediaTime:lastMeta.mediaTime,readyState:video.readyState,origin,...rgb(sample(video))});
  };
  video.addEventListener('loadeddata',()=>recordDrawable('loadeddata'));
  const recordReceiver=frame=>{
    try{keep(receiver,{timestamp:frame.timestamp,width:frame.displayWidth,height:frame.displayHeight,...rgb(sample(frame))});}
    finally{frame.close();}
  };
  const attachTrack=track=>{
    if(trackReader){receiverError='Duplicate received video track';return;}
    try{trackReader=new MediaStreamTrackProcessor({track,maxBufferSize:1}).readable.getReader();}
    catch(error){receiverError=String(error).slice(0,160);return;}
    trackPump=(async()=>{try{while(true){const item=await trackReader.read();if(item.done)break;recordReceiver(item.value);}}
      catch(error){receiverError=String(error).slice(0,160);}})();
  };
  const observe=(now,meta)=>{
    lastMeta=meta;
    const content=sample(video);
    keep(frames,{presented:meta.presentedFrames,width:meta.width,height:meta.height,
      old:content.old.slice(0,3),fresh:content.fresh.slice(0,3),last:content.last.slice(0,3),background:content.background.slice(0,3),
      alpha:content.background[3],videoWidth:video.videoWidth,videoHeight:video.videoHeight,
      readyState:video.readyState,paused:video.paused,currentTime:video.currentTime,
      mediaTime:meta.mediaTime,processingDuration:meta.processingDuration});
    recordDrawable('frame_callback');
    requestAnimationFrame(()=>keep(presentation,{presented:meta.presentedFrames,mediaTime:meta.mediaTime,
      currentTime:video.currentTime,...sample(video)}));
    video.requestVideoFrameCallback(observe);
  };
  return {video,frames,source,decoded,encoded,drawable,receiver,observe,attachTrack,recordReceiver,
    close:async()=>{if(trackReader)await trackReader.cancel();await trackPump;if(receiverError)throw new Error(receiverError);},
    recordSource:(canvas,timestamp)=>keep(source,fixture(canvas,timestamp,"painted source")),
    recordDecoded:frame=>{try{keep(decoded,fixture(frame,frame.timestamp,"direct decoded frame"));}finally{frame.close();}},
    recordEncoded:chunk=>{let checksum=2166136261;for(const byte of chunk.data)checksum=Math.imul(checksum^byte,16777619)>>>0;
      keep(encoded,{type:chunk.type,bytes:chunk.data.length,checksum});},
    snapshot:()=>({frames,presentation,source,decoded,encoded,drawable,receiver,receiverError})};
}
