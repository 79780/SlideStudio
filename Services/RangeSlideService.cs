using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Caching.Memory;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SlideStudio.Models;
using static SlideStudio.Services.TiffRangeReader;

namespace SlideStudio.Services;

public sealed record SlideRegion(double X, double Y, double Width, double Height, TiffDirectory[] Levels);
public sealed record SlideDocument(BlobReference Blob, bool LittleEndian, bool BigTiff, SlideInfoDto Info, SlideRegion[] Regions);
public sealed record StoredTile(int Region, int Ifd, long Column, long Row, long Offset, int Count);
public sealed record TilePlan(int Level, int X, int Y, int Width, int Height, IReadOnlyList<StoredTile> Tiles);
public sealed record RenderedTile(byte[] Jpeg, ReadTrace Trace, TilePlan Plan, string ETag);

public sealed class RangeSlideService(IRangeBlobSource source) : IDisposable
{
    private readonly MemoryCache documents = new(new MemoryCacheOptions { SizeLimit = 2 });
    private readonly MemoryCache outputs = new(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });
    private readonly SemaphoreSlim gate = new(1);

    private async Task<SlideDocument> Document(string name, ReadTrace trace, CancellationToken ct)
    {
        var blob = await source.DescribeAsync(name, ct);
        var key = $"{name}:{blob.ETag}";
        if (documents.TryGetValue(key, out SlideDocument? cached)) return cached!;
        var reader = new TiffRangeReader(source, blob, trace, ct); await reader.Parse();
        var dirs = reader.Directories;
        foreach (var dir in dirs)
        {
            dir.Width = await reader.Value(dir, 256); dir.Height = await reader.Value(dir, 257);
            if (dir.Width <= 0 || dir.Height <= 0 || dir.Width > 1_000_000_000 || dir.Height > 1_000_000_000) throw Bad("Invalid TIFF dimensions.");
        }
        var description = await reader.Description(dirs[0]);
        SlideRegion[] regions; long width, height; string vendor;
        if (description.Contains("<scn") && description.Contains("leica-microsystems.com/scn"))
        {
            using var xr = XmlReader.Create(new StringReader(description), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
            var xml = XDocument.Load(xr); var ns = xml.Root!.Name.Namespace;
            var collection = xml.Root.Element(ns + "collection") ?? throw Bad("Leica collection is missing.");
            double Attr(XElement e, string a) { if (!double.TryParse((string?)e.Attribute(a), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v) || v < 0) throw Bad($"Invalid Leica {a}."); return v; }
            var images = collection.Elements(ns + "image").Select(e => {
                var view = e.Element(ns + "view") ?? throw Bad("Leica view is missing.");
                var dimensions = (e.Element(ns + "pixels") ?? throw Bad("Leica pixels are missing.")).Elements(ns + "dimension").OrderBy(d => Attr(d, "r")).ToArray();
                if (dimensions.Length == 0 || dimensions.GroupBy(d => Attr(d, "r")).Any(g => g.Count() != 1)) throw Bad("Only single-plane brightfield Leica images are supported.");
                var levels = dimensions.Select(d => { var ix = Attr(d, "ifd"); if (ix != Math.Truncate(ix) || ix >= dirs.Count) throw Bad("Invalid Leica IFD reference."); return dirs[(int)ix]; }).ToArray();
                double w = Attr(view, "sizeX"), h = Attr(view, "sizeY");
                if (w == 0 || h == 0) throw Bad("Invalid Leica physical dimensions.");
                return new { X = Attr(view, "offsetX"), Y = Attr(view, "offsetY"), W = w, H = h, Levels = levels, Resolution = w / levels[0].Width };
            }).ToArray();
            if (images.Length == 0) throw Bad("No Leica images found.");
            double nmPerPixel = images.Min(i => i.Resolution);
            var tissue = images.Where(i => Math.Abs(i.Resolution / nmPerPixel - 1) < .001).ToArray();
            // Lowest-resolution macro images are excluded from the diagnostic tissue pyramid.
            if (tissue.Any(i => Math.Abs((i.H / i.Levels[0].Height) / nmPerPixel - 1) > .001)) throw Bad("Anisotropic Leica sampling is not supported.");
            width = checked((long)Math.Ceiling(Attr(collection, "sizeX") / nmPerPixel));
            height = checked((long)Math.Ceiling(Attr(collection, "sizeY") / nmPerPixel));
            regions = tissue.Select(i => new SlideRegion(i.X / nmPerPixel, i.Y / nmPerPixel, i.W / nmPerPixel, i.H / nmPerPixel, i.Levels)).ToArray();
            vendor = "leica";
        }
        else
        {
            // Tiled JPEG SVS/generic TIFF. Associated stripped thumbnail/label images are not levels.
            var pyramid = dirs.Where(d => d.Fields.ContainsKey(324)).OrderByDescending(d => d.Width).ToArray();
            if (pyramid.Length == 0) throw Bad("A tiled JPEG TIFF pyramid is required; stripped images are unsupported.");
            width = pyramid[0].Width; height = pyramid[0].Height;
            pyramid = pyramid.Where(d => Math.Abs((double)d.Width / d.Height / ((double)width / height) - 1) < .02).ToArray();
            regions = [new(0, 0, width, height, pyramid)]; vendor = description.StartsWith("Aperio") ? "aperio" : "tiff";
        }
        if (width <= 0 || height <= 0 || width > 1_000_000_000 || height > 1_000_000_000) throw Bad("Invalid slide canvas dimensions.");
        int count = regions.Min(r => r.Levels.Length);
        foreach (var d in regions.SelectMany(r => r.Levels.Take(count)).Distinct())
        {
            if (await reader.Value(d, 259) != 7) throw Bad($"IFD {d.Index}: only JPEG compression 7 is currently supported (JPEG 2000/LZW need additional codecs).");
            if (await reader.Value(d, 284, fallback: 1) != 1 || await reader.Value(d, 277) != 3 || await reader.Value(d, 274, fallback: 1) != 1 || await reader.Value(d, 262) is not (2 or 6)) throw Bad("Only top-left, interleaved RGB/YCbCr JPEG tiles are supported.");
            if (!d.Fields.TryGetValue(258, out var bits) || bits.Count != 3) throw Bad("Expected three 8-bit samples.");
            for (int i = 0; i < 3; i++) if (await reader.Value(d, 258, i) != 8) throw Bad("Only 8-bit JPEG samples are supported.");
            d.TileWidth = checked((int)await reader.Value(d, 322)); d.TileHeight = checked((int)await reader.Value(d, 323));
            if (d.TileWidth <= 0 || d.TileHeight <= 0 || (long)d.TileWidth * d.TileHeight > 4_194_304) throw Bad("Stored tile exceeds the 4-megapixel decode limit.");
            long tileCount = checked(((d.Width + d.TileWidth - 1) / d.TileWidth) * ((d.Height + d.TileHeight - 1) / d.TileHeight));
            if (!d.Fields.TryGetValue(324, out var offsets) || !d.Fields.TryGetValue(325, out var counts) || offsets.Count != tileCount || counts.Count != tileCount) throw Bad("Invalid TIFF tile index.");
            d.JpegTables = await reader.Bytes(d, 347);
        }
        var first = regions[0]; var levels = new List<SlideLevelDto>();
        for (int i = 0; i < count; i++)
        {
            double ds = i == 0 ? 1 : (first.Width / first.Levels[i].Width + first.Height / first.Levels[i].Height) / 2;
            if (i > 0 && ds <= levels[^1].Downsample) throw Bad("Pyramid levels must decrease in resolution.");
            levels.Add(new(i, (long)Math.Ceiling(width / ds), (long)Math.Ceiling(height / ds), ds));
        }
        var info = new SlideInfoDto(name, vendor, width, height, count, levels, new Dictionary<string, string> {
            ["decoder"] = "TIFF byte ranges + ImageSharp JPEG (no OpenSlide)", ["storage"] = "HTTP Range", ["blob.etag"] = blob.ETag,
            ["blob.bytes"] = blob.Length.ToString(CultureInfo.InvariantCulture), ["tissue.regions"] = regions.Length.ToString(), ["container"] = reader.BigTiff ? "BigTIFF" : "TIFF"
        }, []);
        var doc = new SlideDocument(blob, reader.LittleEndian, reader.BigTiff, info, regions);
        documents.Set(key, doc, new MemoryCacheEntryOptions().SetSize(1)); return doc;
    }

    public async Task<SlideInfoDto> GetInfo(string name, CancellationToken ct) => (await Document(name, new(), ct)).Info;
    private TiffRangeReader Reader(SlideDocument doc, ReadTrace trace, CancellationToken ct) { var r = new TiffRangeReader(source, doc.Blob, trace, ct); r.UseFormat(doc.LittleEndian, doc.BigTiff); return r; }

    private async Task<TilePlan> Plan(SlideDocument doc, int level, int x, int y, ReadTrace trace, CancellationToken ct)
    {
        if (level < 0 || level >= doc.Info.LevelCount || x < 0 || y < 0) throw new SlideApiException(400, "Invalid tile coordinates.");
        var l = doc.Info.Levels[level]; long px = (long)x * 256, py = (long)y * 256;
        if (px >= l.Width || py >= l.Height) throw new SlideApiException(400, "Tile is outside the slide.");
        int w = (int)Math.Min(256, l.Width - px), h = (int)Math.Min(256, l.Height - py);
        double left = px * l.Downsample, top = py * l.Downsample, right = (px + w) * l.Downsample, bottom = (py + h) * l.Downsample;
        var reader = Reader(doc, trace, ct); var tiles = new List<StoredTile>();
        for (int ri = 0; ri < doc.Regions.Length; ri++)
        {
            var r = doc.Regions[ri]; var d = r.Levels[level];
            double il = Math.Max(left, r.X), it = Math.Max(top, r.Y), ir = Math.Min(right, r.X + r.Width), ib = Math.Min(bottom, r.Y + r.Height);
            if (il >= ir || it >= ib) continue;
            long x0 = (long)Math.Floor((il - r.X) / r.Width * d.Width / d.TileWidth), y0 = (long)Math.Floor((it - r.Y) / r.Height * d.Height / d.TileHeight);
            long x1 = (long)Math.Ceiling((ir - r.X) / r.Width * d.Width / d.TileWidth), y1 = (long)Math.Ceiling((ib - r.Y) / r.Height * d.Height / d.TileHeight);
            long across = (d.Width + d.TileWidth - 1) / d.TileWidth;
            x1 = Math.Min(x1, across); y1 = Math.Min(y1, (d.Height + d.TileHeight - 1) / d.TileHeight);
            for (long row = y0; row < y1; row++) for (long col = x0; col < x1; col++)
            {
                if (tiles.Count >= 64) throw Bad("Requested output requires too many source tiles.");
                long index = checked(row * across + col);
                long offset = await reader.Value(d, 324, index), length = await reader.Value(d, 325, index);
                if (length <= 0 || length > 16 * 1024 * 1024 || offset < 0 || offset > doc.Blob.Length - length) throw Bad("Invalid compressed tile byte range.");
                tiles.Add(new(ri, d.Index, col, row, offset, (int)length));
            }
        }
        return new(level, x, y, w, h, tiles);
    }

    public async Task<object> GetPlan(string name, int level, int x, int y, CancellationToken ct)
    {
        var trace = new ReadTrace(); var doc = await Document(name, trace, ct); var plan = await Plan(doc, level, x, y, trace, ct);
        return new { decoder = "TIFF byte ranges + ImageSharp JPEG", blob = doc.Blob, plan, compressedTileBytes = plan.Tiles.Sum(t => (long)t.Count), metadataReads = trace.Reads };
    }

    public async Task<(RenderedTile Tile, bool Hit)> Render(string name, int level, int x, int y, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var trace = new ReadTrace(); var doc = await Document(name, trace, ct);
            var key = $"{name}:{doc.Blob.ETag}:{level}:{x}:{y}";
            if (outputs.TryGetValue(key, out RenderedTile? cached)) return (cached! with { Trace = trace }, true);
            var plan = await Plan(doc, level, x, y, trace, ct);
            using var output = new Image<Rgb24>(plan.Width, plan.Height, new Rgb24(255, 255, 255));
            var ds = doc.Info.Levels[level].Downsample;
            foreach (var tile in plan.Tiles)
            {
                ct.ThrowIfCancellationRequested();
                var r = doc.Regions[tile.Region]; var d = r.Levels[level];
                var compressed = new byte[tile.Count];
                for (int pos = 0; pos < tile.Count; pos += HttpRangeBlobSource.MaxRange)
                {
                    var chunk = await source.ReadAsync(doc.Blob, tile.Offset + pos, Math.Min(HttpRangeBlobSource.MaxRange, tile.Count - pos), trace, ct);
                    chunk.CopyTo(compressed, pos);
                }
                var jpeg = CompleteJpeg(compressed, d.JpegTables);
                var identified = Image.Identify(jpeg) ?? throw Bad("Invalid JPEG tile.");
                if (identified.Width != d.TileWidth || identified.Height != d.TileHeight) throw Bad("JPEG tile dimensions disagree with TIFF metadata.");
                using var image = Image.Load<Rgb24>(jpeg);
                // Work in original slide coordinates so saved annotations keep their meaning.
                for (int yy = 0; yy < plan.Height; yy++)
                {
                    double gy = ((long)y * 256 + yy + .5) * ds;
                    if (gy < r.Y || gy >= r.Y + r.Height) continue;
                    long sy = (long)Math.Floor((gy - r.Y) / r.Height * d.Height);
                    if (sy / d.TileHeight != tile.Row) continue;
                    for (int xx = 0; xx < plan.Width; xx++)
                    {
                        double gx = ((long)x * 256 + xx + .5) * ds;
                        if (gx < r.X || gx >= r.X + r.Width) continue;
                        long sx = (long)Math.Floor((gx - r.X) / r.Width * d.Width);
                        if (sx / d.TileWidth == tile.Column) output[xx, yy] = image[(int)(sx % d.TileWidth), (int)(sy % d.TileHeight)];
                    }
                }
            }
            using var stream = new MemoryStream(); output.Save(stream, new JpegEncoder { Quality = 90 });
            var result = new RenderedTile(stream.ToArray(), trace, plan, doc.Blob.ETag);
            outputs.Set(key, result, new MemoryCacheEntryOptions().SetSize(result.Jpeg.Length).SetSlidingExpiration(TimeSpan.FromMinutes(10)));
            return (result, false);
        }
        finally { gate.Release(); }
    }

    private static byte[] CompleteJpeg(byte[] tile, byte[] tables)
    {
        if (tile.Length < 4 || tile[0] != 255 || tile[1] != 216) throw Bad("Stored tile is not a JPEG stream.");
        if (tables.Length == 0) return tile;
        if (tables.Length < 4 || tables[0] != 255 || tables[1] != 216 || tables[^2] != 255 || tables[^1] != 217) throw Bad("Invalid shared JPEG tables.");
        // Remove tables' EOI and tile's SOI; tile-local tables may override shared tables.
        var result = new byte[tables.Length + tile.Length - 4];
        Array.Copy(tables, result, tables.Length - 2); Array.Copy(tile, 2, result, tables.Length - 2, tile.Length - 2); return result;
    }
    public void Dispose() { documents.Dispose(); outputs.Dispose(); gate.Dispose(); }
}
