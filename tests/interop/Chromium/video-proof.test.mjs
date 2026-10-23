import test from 'node:test';
import assert from 'node:assert/strict';
import {createVideoProof} from './video-proof.mjs';

test('video diagnostics distinguish callback pixels from later presentation without replacing evidence',()=>{
  const callbacks=[];
  const video={pixel:0,videoWidth:320,videoHeight:240,readyState:2,currentTime:0,paused:false,addEventListener(){},requestVideoFrameCallback(){}};
  globalThis.document={createElement:()=>video,body:{appendChild(){}}};
  globalThis.requestAnimationFrame=callback=>callbacks.push(callback);
  globalThis.OffscreenCanvas=class {
    getContext(){let image;return {clearRect(){image=null;},drawImage(value){image=value;},getImageData(x){const value=image.patch && x===24?0:image.pixel;return {data:new Uint8ClampedArray([value,value,value,255])};}};}
  };
  try {
    const proof=createVideoProof();
    proof.recordSource({pixel:128,patch:true},0);
    assert.throws(()=>proof.recordSource({pixel:0},0),/painted source synthetic content mismatch/);
    proof.observe(1,{presentedFrames:1,width:320,height:240,mediaTime:0});
    video.pixel=128;callbacks.shift()();
    assert.deepEqual(proof.snapshot().frames[0].fresh,[0,0,0]);
    assert.deepEqual(proof.snapshot().presentation[0].fresh,[128,128,128,255]);
    assert.deepEqual(proof.snapshot().source[0].fresh,[128,128,128,255]);
    let closed=false;proof.recordDecoded({pixel:128,patch:true,timestamp:0,close(){closed=true;}});assert.ok(closed);
    for(let i=0;i<100;i++)proof.recordEncoded({type:'key',data:new Uint8Array([1,2,3])});
    assert.equal(proof.snapshot().encoded.length,64);
    assert.deepEqual(Object.keys(proof.snapshot().encoded[0]).sort(),['bytes','checksum','type']);
  } finally {
    delete globalThis.document;delete globalThis.requestAnimationFrame;delete globalThis.OffscreenCanvas;
  }
});


test('an early transparent callback is preserved until real current data exists; receiver frames are independently checked and closed',async()=>{
  const events=new Map();let cancelled=false,frameClosed;
  const consumed=new Promise(resolve=>frameClosed=resolve);
  const video={pixel:0,alpha:0,videoWidth:320,videoHeight:240,readyState:0,currentTime:0,paused:false,
    addEventListener:(name,callback)=>events.set(name,callback),requestVideoFrameCallback(){}};
  globalThis.document={createElement:()=>video,body:{appendChild(){}}};
  globalThis.requestAnimationFrame=()=>{};
  globalThis.OffscreenCanvas=class {
    getContext(){let image;return {clearRect(){image=null;},drawImage(value){image=value;},getImageData(x){
      const v=image.patch && x===24?0:image.pixel;return {data:new Uint8ClampedArray([v,v,v,image.alpha??255])};}};}
  };
  globalThis.MediaStreamTrackProcessor=class {
    constructor(options){assert.equal(options.maxBufferSize,1);this.readable=new ReadableStream({start(controller){
      controller.enqueue({pixel:128,patch:true,displayWidth:320,displayHeight:240,timestamp:0,close:frameClosed});
    },cancel(){cancelled=true;}});}
  };
  try {
    const proof=createVideoProof();proof.attachTrack({});await consumed;
    proof.observe(0,{presentedFrames:1,width:320,height:240,mediaTime:0});
    assert.equal(proof.frames[0].alpha,0);assert.equal(proof.drawable.length,0);
    assert.deepEqual(proof.receiver[0].fresh,[128,128,128]);assert.equal(proof.receiver[0].alpha,255);
    // No redraw based on time passing, retries, or decoder frame counts.
    assert.equal(proof.drawable.length,0);
    video.readyState=2;video.pixel=128;video.alpha=255;video.patch=true;
    events.get('loadeddata')();
    assert.equal(proof.drawable.length,1);assert.equal(proof.drawable[0].origin,'loadeddata');
    assert.deepEqual(proof.drawable[0].old,[0,0,0]);assert.deepEqual(proof.drawable[0].fresh,[128,128,128]);
    assert.equal(proof.frames[0].alpha,0); // Raw evidence is never replaced.
    video.pixel=0;video.patch=false;proof.observe(1,{presentedFrames:2,width:320,height:240,mediaTime:1});
    assert.deepEqual(proof.drawable[1].fresh,[0,0,0]); // Opaque black remains a failed content sample.
    await proof.close();assert.ok(cancelled);
  } finally {
    delete globalThis.document;delete globalThis.requestAnimationFrame;delete globalThis.OffscreenCanvas;delete globalThis.MediaStreamTrackProcessor;
  }
});
