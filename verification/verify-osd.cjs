// Unit/integration checks for the real bundled library's geometry; no browser automation.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const element = () => ({style:{},getContext:()=>null,addEventListener(){},removeEventListener(){},appendChild(){},setAttribute(){},getElementsByTagName:()=>[]});
const document = {createElement:element,documentElement:element(),body:element(),addEventListener(){},removeEventListener(){}};
const window = {document,navigator:{userAgent:'node-unit-test',appName:'Netscape',appVersion:'1'},location:{protocol:'http:',search:''},addEventListener(){},removeEventListener(){},setTimeout,clearTimeout,console,devicePixelRatio:1};
window.window=window; window.self=window;
const context=vm.createContext({self:window,window,document,navigator:window.navigator,location:window.location,console,setTimeout,clearTimeout,setInterval,clearInterval,Promise,Map,Set,WeakMap,WeakSet,Uint8Array,ArrayBuffer,URL,Image:function(){},HTMLElement:function(){},HTMLCanvasElement:function(){}});
try { vm.runInContext(fs.readFileSync(path.join(__dirname,'../wwwroot/vendor/openseadragon/openseadragon.min.js'),'utf8'),context); }
catch(e) { console.error(e.name+': '+e.message); process.exit(1); }
const OSD=context.OpenSeadragon||window.OpenSeadragon;
const createSource=require('../wwwroot/tile-source.js');
(async()=>{
  assert.equal(OSD.version.versionStr,'6.1.1');
  let requests=0;
  for(const name of ['Leica-1.scn','Leica-2.scn']){
    const base='http://127.0.0.1:5188/api/slides/'+name;
    const info=await (await fetch(base+'/info')).json();
    const source=createSource(OSD,info,base);
    assert.equal(source.getLevelScale(source.maxLevel),1);
    for(let level=0;level<=source.maxLevel;level++){
      const native=info.levels[info.levelCount-1-level],n=source.getNumTiles(level);
      assert.equal(source.getLevelScale(level),1/native.downsample);
      assert.equal(n.x,Math.ceil(native.width/256));assert.equal(n.y,Math.ceil(native.height/256));
      const testTiles=[[0,0],[n.x-1,n.y-1],[Math.floor(n.x/2),Math.floor(n.y/2)]];
      for(const [x,y] of testTiles){
        const url=source.getTileUrl(level,x,y);
        assert.ok(url.includes(`/tiles/${native.level}/${x}/${y}?v=`));
        const bounds=source.getTileBounds(level,x,y,false);
        assert.ok(bounds.width>0&&bounds.height>0);
        assert.ok(bounds.x+bounds.width<=1+1e-9);
        assert.ok(bounds.y+bounds.height<=info.height/info.width+1e-9);
        // Exact coordinate mapping used by saved annotations must agree across pyramid levels.
        assert.ok(Math.abs(bounds.x*info.width-x*256*native.downsample)<1e-6);
        const midpoint=new OSD.Point(bounds.x+bounds.width/2,bounds.y+bounds.height/2);
        const tile=source.getTileAtPoint(level,midpoint);assert.equal(tile.x,x);assert.equal(tile.y,y);
        const response=await fetch(url);assert.equal(response.status,200,url);
        assert.equal(response.headers.get('X-Decoder'),'TIFF-Range-ImageSharp');
        const bytes=new Uint8Array(await response.arrayBuffer());assert.equal(bytes[0],255);assert.equal(bytes[1],216);requests++;
      }
    }
    console.log(`PASS ${name}: real OpenSeadragon level scales, reversed URLs, edge bounds, tile lookup and API JPEGs.`);
  }
  console.log(`PASS ${requests} OpenSeadragon-generated API requests; pinned library ${OSD.version.versionStr}.`);
})().catch(e=>{console.error(e);process.exitCode=1;});
