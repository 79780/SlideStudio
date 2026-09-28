'use strict';
const $ = id => document.getElementById(id);
const stage = $('stage'), overlay = $('annotationOverlay');
let slide = null, info = null, notes = [], dirty = false, generation = 0;
let mode = 'pan', grid = false, draft = null, drag = null, frame = 0;
let rangeBytes = 0, rangeRequests = 0, source = null;
const status = message => $('status').textContent = message;
const api = () => `/api/slides/${encodeURIComponent(slide)}`;

// OpenSeadragon owns image loading, drawing, navigation and tile caching.
const viewer = OpenSeadragon({
  id: 'slide-viewer', drawer: 'canvas',
  showNavigationControl: false, showNavigator: true,
  navigatorPosition: 'TOP_RIGHT', navigatorWidth: 130, navigatorHeight: 170,
  navigatorAutoFade: false, navigatorBorderColor: '#425c6c',
  navigatorDisplayRegionColor: '#6fe0c6',
  loadTilesWithAjax: true, imageLoaderLimit: 4, maxImageCacheCount: 160,
  immediateRender: true, minPixelRatio: 1, blendTime: 0.15, animationTime: 0.35,
  minZoomImageRatio: 0.5, maxZoomPixelRatio: 8, visibilityRatio: 0.2,
  gestureSettingsMouse: { clickToZoom: false, dblClickToZoom: true },
  placeholderFillStyle: '#ffffff'
});
const item = () => viewer.world.getItemAt(0);
const loaded = () => info && item();
async function json(url, options) {
  const response = await fetch(url, options);
  if (!response.ok) { let error; try { error = (await response.json()).error; } catch {} throw new Error(error || `Request failed (${response.status})`); }
  return response.json();
}
function schedule() { if (!frame) frame = requestAnimationFrame(() => { frame = 0; drawOverlay(); }); }
function screenPoint(x, y) {
  return viewer.viewport.pixelFromPoint(item().imageToViewportCoordinates(new OpenSeadragon.Point(x, y), true), true);
}
function imagePoint(event) {
  const rect = stage.getBoundingClientRect();
  const viewportPoint = viewer.viewport.pointFromPixel(new OpenSeadragon.Point(event.clientX - rect.left, event.clientY - rect.top), true);
  const p = item().viewportToImageCoordinates(viewportPoint, true);
  return { x: Math.max(0, Math.min(info.width, p.x)), y: Math.max(0, Math.min(info.height, p.y)) };
}
function svg(tag, attributes, text) {
  const element = document.createElementNS('http://www.w3.org/2000/svg', tag);
  for (const [key, value] of Object.entries(attributes)) element.setAttribute(key, value);
  if (text !== undefined) element.textContent = text;
  return element;
}
function drawnTiles() { return loaded() ? item().lastDrawn.map(entry => entry.tile) : []; }
function drawOverlay() {
  overlay.replaceChildren();
  if (!loaded()) { $('metrics').textContent = 'OpenSeadragon | Waiting for slide'; return; }
  const tiles = drawnTiles();
  if (grid) for (const tile of tiles) {
    const l = source.apiLevels[tile.level], ds = l.downsample;
    const a = screenPoint(tile.x * 256 * ds, tile.y * 256 * ds);
    const b = screenPoint(Math.min(info.width, (tile.x + 1) * 256 * ds), Math.min(info.height, (tile.y + 1) * 256 * ds));
    overlay.append(svg('rect', {x:a.x,y:a.y,width:b.x-a.x,height:b.y-a.y,fill:'none',stroke:'#159c9a','stroke-width':1}));
    overlay.append(svg('text', {x:a.x+5,y:a.y+15,fill:'#075b59','font-size':11}, `${l.level} / ${tile.x}, ${tile.y}`));
  }
  for (const n of [...notes, ...(draft ? [draft] : [])]) {
    const a = screenPoint(n.x, n.y), b = screenPoint(n.x+n.w, n.y+n.h);
    if (b.x < 0 || b.y < 0 || a.x > stage.clientWidth || a.y > stage.clientHeight) continue;
    overlay.append(svg('rect', {x:a.x,y:a.y,width:b.x-a.x,height:b.y-a.y,fill:'#edaa3622',stroke:'#edaa36','stroke-width':2}));
    overlay.append(svg('text', {x:a.x+5,y:a.y+16,fill:'#bf7510','font-size':12,'font-weight':600}, n.label));
  }
  const levelNames = [...new Set(tiles.map(t => source.apiLevels[t.level].level))].sort((a,b)=>a-b);
  const scale = item().viewportToImageZoom(viewer.viewport.getZoom(true));
  $('metrics').textContent = `OpenSeadragon | Level ${levelNames.join(', ') || '-'} | ${tiles.length} drawn tiles | ${(scale*100).toFixed(1)}%`;
}
for (const event of ['animation','animation-finish','update-viewport','resize','tile-drawn']) viewer.addHandler(event, schedule);
viewer.addHandler('open', () => { viewer.viewport.goHome(true); setMode('pan'); schedule(); status(`${slide} | OpenSeadragon ready`); });
viewer.addHandler('open-failed', e => status(e.message || 'Unable to open slide'));
viewer.addHandler('tile-load-failed', () => status('A tile could not load. Reopen the slide to retry.'));
let countedRequests = new WeakSet();
function countTileRequest(e) {
  if (e.tiledImage.source !== source || !e.tileRequest || countedRequests.has(e.tileRequest)) return;
  countedRequests.add(e.tileRequest);
  rangeBytes += Number(e.tileRequest.getResponseHeader('X-Source-Bytes') || 0);
  rangeRequests += Number(e.tileRequest.getResponseHeader('X-Range-Requests') || 0);
  $('rangeStats').textContent = `${rangeBytes.toLocaleString()} source bytes / ${rangeRequests} HTTP range GETs this session`;
  schedule();
}
viewer.addHandler('tile-loaded', countTileRequest);
viewer.navigator.addHandler('tile-loaded', countTileRequest);
new ResizeObserver(schedule).observe(stage);

function setMode(next) {
  mode = next; drag = null; draft = null;
  viewer.setMouseNavEnabled(mode === 'pan');
  overlay.classList.toggle('drawing', mode === 'rectangle');
  $('pan').classList.toggle('active', mode === 'pan');
  $('annotate').classList.toggle('active', mode === 'rectangle');
  $('pan').setAttribute('aria-pressed', mode === 'pan');
  $('annotate').setAttribute('aria-pressed', mode === 'rectangle');
  schedule();
}
function changed() { dirty = true; $('saveState').textContent = 'Unsaved changes'; renderNotes(); schedule(); }
function renderNotes() {
  $('noteCount').textContent = notes.length; $('notes').replaceChildren();
  notes.forEach((n, i) => {
    const row = document.createElement('div'); row.className = 'note';
    const label = document.createElement('strong'); label.textContent = n.label;
    const go = document.createElement('button'); go.textContent = 'Locate';
    go.onclick = () => {
      if (!loaded()) return;
      const rect = item().imageToViewportRectangle(n.x-n.w*.3, n.y-n.h*.3, n.w*1.6, n.h*1.6);
      viewer.viewport.fitBoundsWithConstraints(rect); schedule();
    };
    const del = document.createElement('button'); del.textContent = 'Delete';
    del.onclick = () => { notes.splice(i,1); changed(); };
    row.append(label,go,del); $('notes').append(row);
  });
}
async function openSlide(name) {
  if (dirty && !confirm('Discard unsaved annotations?')) return;
  const g = ++generation;
  viewer.close(); source=null; slide=name; info=null; notes=[]; dirty=false; draft=null; drag=null;
  rangeBytes=0; rangeRequests=0; countedRequests=new WeakSet();
  $('rangeStats').textContent='No source ranges fetched yet.';
  $('saveState').textContent='No unsaved changes'; $('details').textContent='Opening slide...';
  renderNotes(); schedule(); status(`Opening ${name}...`);
  try {
    const [metadata, annotations] = await Promise.all([json(api()+'/info'),json(api()+'/annotations')]);
    if(g !== generation) return;
    info=metadata; notes=annotations; renderNotes();
    $('details').textContent=`${info.vendor || 'Slide'} | ${info.width.toLocaleString()} x ${info.height.toLocaleString()} px | ${info.levelCount} pyramid levels`;
    $('hex').textContent='No bytes requested.';
    document.querySelectorAll('.slide').forEach(b=>b.classList.toggle('active',b.dataset.name===name));
    source=createApiTileSource(OpenSeadragon,info,api()); viewer.open(source);
  } catch(e) { if(g===generation) status(e.message); }
}

stage.addEventListener('pointermove', event => {
  if(!loaded()) return;
  const p=imagePoint(event);
  $('position').textContent=`X ${Math.round(p.x).toLocaleString()} | Y ${Math.round(p.y).toLocaleString()}`;
});
overlay.addEventListener('pointerdown', event => {
  if(!loaded() || mode!=='rectangle' || event.button!==0 || drag) return;
  event.preventDefault(); drag={id:event.pointerId,start:imagePoint(event)};
  overlay.setPointerCapture(event.pointerId);
});
overlay.addEventListener('pointermove', event => {
  if(!drag || drag.id!==event.pointerId || !loaded()) return;
  const p=imagePoint(event), a=drag.start;
  draft={x:Math.min(a.x,p.x),y:Math.min(a.y,p.y),w:Math.abs(a.x-p.x),h:Math.abs(a.y-p.y),label:$('label').value.trim()||'Region of interest'};
  schedule();
});
overlay.addEventListener('pointerup', event => {
  if(!drag || drag.id!==event.pointerId) return;
  if(draft) {
    const a=screenPoint(draft.x,draft.y), b=screenPoint(draft.x+draft.w,draft.y+draft.h);
    if(b.x-a.x>4 && b.y-a.y>4) { notes.push(draft); changed(); }
  }
  drag=null; draft=null; overlay.releasePointerCapture(event.pointerId); schedule();
});
for(const event of ['pointercancel','lostpointercapture']) overlay.addEventListener(event,()=>{drag=null;draft=null;schedule();});
document.addEventListener('keydown',event=>{if(event.key==='Escape')setMode('pan');});
$('pan').onclick=()=>setMode('pan'); $('annotate').onclick=()=>setMode('rectangle');
$('fit').onclick=()=>{if(loaded())viewer.viewport.goHome();};
function zoom(factor) {if(loaded()){viewer.viewport.zoomBy(factor);viewer.viewport.applyConstraints();}}
$('plus').onclick=()=>zoom(1.5); $('minus').onclick=()=>zoom(1/1.5);
$('actual').onclick=()=>{if(loaded()){viewer.viewport.zoomTo(item().imageToViewportZoom(1));viewer.viewport.applyConstraints();}};
$('grid').onclick=()=>{grid=!grid;$('grid').classList.toggle('active',grid);$('grid').setAttribute('aria-pressed',grid);schedule();};
$('save').onclick=async()=>{
  if(!info)return;
  const name=slide,snapshot=JSON.stringify(notes);$('save').disabled=true;
  try {await json(api()+'/annotations',{method:'PUT',headers:{'Content-Type':'application/json'},body:snapshot});
    if(slide===name&&snapshot===JSON.stringify(notes)){dirty=false;$('saveState').textContent='Saved to SQLite';}
  }catch(e){status('Save failed: '+e.message);}finally{$('save').disabled=false;}
};
$('export').onclick=()=>{
  if(!info)return;const a=document.createElement('a');
  a.href=URL.createObjectURL(new Blob([JSON.stringify({slide,coordinateSystem:'level-0 pixels',annotations:notes},null,2)],{type:'application/json'}));
  a.download=slide+'.annotations.json';a.click();setTimeout(()=>URL.revokeObjectURL(a.href),1000);
};
$('read').onclick=async()=>{
  if(!info)return;const g=generation;
  try{const r=await fetch(api()+`/bytes?offset=${encodeURIComponent($('offset').value)}&count=${encodeURIComponent($('count').value)}`);
    if(!r.ok)throw Error((await r.json()).error||'Range failed');const b=new Uint8Array(await r.arrayBuffer());if(g!==generation)return;
    $('hex').textContent=r.headers.get('Content-Range')+'\n\n'+Array.from(b.slice(0,512),x=>x.toString(16).padStart(2,'0')).join(' ')+(b.length>512?'\n...preview limited to 512 bytes':'');
    status(`Read exactly ${b.length.toLocaleString()} bytes`);
  }catch(e){if(g===generation)status(e.message);}
};
$('inspectTile').onclick=async()=>{
  if(!loaded())return;const g=generation;
  const center=item().viewportToImageCoordinates(viewer.viewport.getCenter(true),true);
  const tiles=drawnTiles().filter(t=>{
    const ds=source.apiLevels[t.level].downsample;
    return center.x>=t.x*256*ds&&center.x<(t.x+1)*256*ds&&center.y>=t.y*256*ds&&center.y<(t.y+1)*256*ds;
  }).sort((a,b)=>b.level-a.level);
  if(!tiles.length){status('Center a loaded tissue tile in the viewer, then inspect it.');return;}
  const tile=tiles[0],level=source.apiLevels[tile.level].level;
  try{const result=await json(api()+`/tile-plan/${level}/${tile.x}/${tile.y}`);if(g!==generation)return;
    $('hex').textContent=`View tile ${level}/${tile.x}/${tile.y}\n${result.compressedTileBytes.toLocaleString()} compressed source bytes\n\n`+result.plan.tiles.map(t=>`IFD ${t.ifd}, stored tile ${t.column},${t.row}\nbytes ${t.offset}-${t.offset+t.count-1} (${t.count})`).join('\n\n')+(result.plan.tiles.length?'':'Background only: no image ranges needed.');
    if(result.plan.tiles.length){$('offset').value=result.plan.tiles[0].offset;$('count').value=Math.min(1048576,result.plan.tiles[0].count);}
  }catch(e){if(g===generation)status(e.message);}
};
window.onbeforeunload=event=>{if(dirty){event.preventDefault();event.returnValue='';}};
json('/api/slides').then(list=>{
  for(const s of list){const b=document.createElement('button');b.className='slide';b.dataset.name=s.name;
    const title=document.createElement('strong');title.textContent=s.name;
    const size=document.createElement('small');size.textContent=(s.bytes/1024/1024).toFixed(1)+' MiB | '+s.name.split('.').pop().toUpperCase();
    b.append(title,size);b.onclick=()=>openSlide(s.name);$('library').append(b);
  }
  if(list.length)openSlide(list[0].name);else status('No supported slide files found in Lab Images.');
}).catch(e=>status(e.message));
