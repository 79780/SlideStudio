import {open} from 'node:fs/promises';
for(const name of ['Leica-1.scn','Leica-2.scn']){
 const f=await open(new URL('../Lab Images/'+name,import.meta.url));
 const read=async(o,n)=>{const b=Buffer.alloc(n);await f.read(b,0,n,o);return b;};
 const h=await read(0,16);const big=h.readUInt16LE(2)===43;let off=big?Number(h.readBigUInt64LE(8)):h.readUInt32LE(4);let i=0;
 while(off){const nbuf=await read(off,big?8:2),n=big?Number(nbuf.readBigUInt64LE()):nbuf.readUInt16LE();const entries=await read(off+(big?8:2),n*(big?20:12)+(big?8:4));const tags={};for(let j=0;j<n;j++){const e=entries.subarray(j*(big?20:12));const tag=e.readUInt16LE(),type=e.readUInt16LE(2),count=big?Number(e.readBigUInt64LE(4)):e.readUInt32LE(4);const sz=({1:1,2:1,3:2,4:4,5:8,7:1,16:8})[type];if(!sz)continue;const pos=big?12:8;const loc=big?Number(e.readBigUInt64LE(pos)):e.readUInt32LE(pos);const b=count*sz<=(big?8:4)?e.subarray(pos,pos+count*sz):await read(loc,Math.min(count*sz,100000));if(tag===270){tags.xml=b.toString('utf8').replaceAll('\0','');}else if([256,257,258,259,262,277,284,322,323,324,325,347].includes(tag)){const value=type===3?b.readUInt16LE():type===16?Number(b.readBigUInt64LE()):type===4?b.readUInt32LE():b[0];tags[tag]={type,count,value};}}
 console.log(name,'IFD',i++,JSON.stringify(tags));off=big?Number(entries.readBigUInt64LE(n*20)):entries.readUInt32LE(n*12);}
 await f.close();
}
