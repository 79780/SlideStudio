/* Native backend levels run full-resolution first; OpenSeadragon runs smallest first. */
(function (root) {
  function createApiTileSource(OSD, info, base) {
    const levels = [...info.levels].reverse();
    if (!levels.length || levels.at(-1).downsample !== 1 ||
        levels.some(l => !Number.isFinite(l.downsample) || l.downsample < 1)) {
      throw new Error('Invalid slide pyramid metadata');
    }
    return new OSD.TileSource({
      width: info.width, height: info.height,
      tileSize: 256, tileOverlap: 0, minLevel: 0, maxLevel: levels.length - 1,
      apiLevels: levels,
      getLevelScale(level) { return 1 / levels[level].downsample; },
      getNumTiles(level) {
        return new OSD.Point(Math.ceil(levels[level].width / 256), Math.ceil(levels[level].height / 256));
      },
      getTileUrl(level, x, y) {
        const version = encodeURIComponent(info.properties?.['blob.etag'] || '');
        return `${base}/tiles/${levels[level].level}/${x}/${y}?v=${version}`;
      },
      hasTransparency() { return false; }
    });
  }
  root.createApiTileSource = createApiTileSource;
  if (typeof module !== 'undefined' && module.exports) module.exports = createApiTileSource;
})(globalThis);
