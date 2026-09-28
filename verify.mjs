import assert from 'node:assert/strict';
import {open} from 'node:fs/promises';
const base='http://127.0.0.1:5188';
const get=async path=>{const r=await fetch(base+path);assert.equal(r.status,200,path);return r.json();};
const slides=await get('/api/slides');assert.ok(slides.length>=2);
for(const s of slides){
 const p='/api/slides/'+encodeURIComponent(s.name),info=await get(p+'/info');assert.ok(info.width>0&&info.levelCount>0);
 const l=info.levels.at(-1),tile=p+`/tiles/${l.level}/0/0`;
 let r=await fetch(base+tile);assert.equal(r.status,200);const jpeg=new Uint8Array(await r.arrayBuffer());assert.equal(jpeg[0],255);assert.equal(jpeg[1],216);
 r=await fetch(base+tile);assert.equal(r.headers.get('x-tile-cache'),'HIT');
 r=await fetch(base+p+'/tiles/999/0/0');assert.equal(r.status,400);
 for(const offset of [0,Math.max(0,s.bytes-64)]){
  r=await fetch(base+p+`/bytes?offset=${offset}&count=64`);assert.equal(r.status,206);const actual=Buffer.from(await r.arrayBuffer());
  const file=await open(new URL('../Lab Images/'+s.name,import.meta.url));try{const expected=Buffer.alloc(64);await file.read(expected,0,64,offset);assert.deepEqual(actual,expected);}finally{await file.close();}
 }
 r=await fetch(base+p+'/bytes?offset=0&count=1048577');assert.equal(r.status,400);
 r=await fetch(base+p+`/bytes?offset=${s.bytes-1}&count=2`);assert.equal(r.status,400);
 const original=await get(p+'/annotations');
 const put=body=>fetch(base+p+'/annotations',{method:'PUT',headers:{'content-type':'application/json'},body:JSON.stringify(body)});
 try{const test=[{x:10,y:20,w:30,h:40,label:'Verification'}];assert.equal((await put(test)).status,200);assert.deepEqual(await get(p+'/annotations'),test);}finally{assert.equal((await put(original)).status,200);}
 console.log(JSON.stringify({slide:s.name,dimensions:[info.width,info.height],levels:info.levelCount,jpegBytes:jpeg.length,checks:'tile, cache, byte equality, bounds, annotation roundtrip passed'}));
}
