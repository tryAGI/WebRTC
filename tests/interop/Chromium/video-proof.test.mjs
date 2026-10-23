import test from 'node:test';
import assert from 'node:assert/strict';
import {createVideoProof} from './video-proof.mjs';

test('video diagnostics distinguish callback pixels from later presentation without replacing evidence',()=>{
  const callbacks=[];
  const video={pixel:0,videoWidth:320,videoHeight:240,readyState:2,currentTime:0,paused:false,requestVideoFrameCallback(){}};
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
