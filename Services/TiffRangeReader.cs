using System.Buffers.Binary;
using System.Text;

namespace SlideStudio.Services;

// TIFF directory parsing only. ImageSharp receives individual JPEG byte arrays, never a slide stream.
public sealed class TiffRangeReader(IRangeBlobSource source, BlobReference blob, ReadTrace trace, CancellationToken ct)
{
    public bool LittleEndian { get; private set; }
    public bool BigTiff { get; private set; }
    public void UseFormat(bool littleEndian, bool bigTiff) { LittleEndian = littleEndian; BigTiff = bigTiff; }
    public List<TiffDirectory> Directories { get; } = [];
    public Task<byte[]> Read(long offset, int count) => source.ReadAsync(blob, offset, count, trace, ct);
    public ulong Number(ReadOnlySpan<byte> bytes, int size) => size switch {
        1 => bytes[0],
        2 => LittleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes),
        4 => LittleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes),
        8 => LittleEndian ? BinaryPrimitives.ReadUInt64LittleEndian(bytes) : BinaryPrimitives.ReadUInt64BigEndian(bytes),
        _ => throw Bad("Unsupported TIFF field type.")
    };
    public static SlideApiException Bad(string message) => new(415, message);
    public static int Size(int type) => type switch { 1 or 2 or 7 => 1, 3 => 2, 4 or 13 => 4, 16 or 18 => 8, _ => 0 };

    public async Task Parse()
    {
        var h = await Read(0, 16);
        if (h[0] != h[1] || (h[0] != 73 && h[0] != 77)) throw Bad("Only TIFF/BigTIFF slide containers are supported.");
        LittleEndian = h[0] == 73;
        var magic = Number(h.AsSpan(2), 2); BigTiff = magic == 43;
        if (magic != 42 && magic != 43) throw Bad("Unsupported TIFF header.");
        if (BigTiff && (Number(h.AsSpan(4), 2) != 8 || Number(h.AsSpan(6), 2) != 0)) throw Bad("Unsupported BigTIFF offset layout.");
        long next = checked((long)Number(h.AsSpan(BigTiff ? 8 : 4), BigTiff ? 8 : 4));
        var seen = new HashSet<long>();
        while (next != 0)
        {
            if (!seen.Add(next) || Directories.Count >= 128) throw Bad("Cyclic or excessive TIFF directories.");
            int prefix = BigTiff ? 8 : 2, entrySize = BigTiff ? 20 : 12, pointerSize = BigTiff ? 8 : 4;
            var countBytes = await Read(next, prefix);
            var count = Number(countBytes, prefix);
            if (count > 4096) throw Bad("Excessive TIFF directory entries.");
            var entries = await Read(checked(next + prefix), checked((int)count * entrySize + pointerSize));
            var dir = new TiffDirectory(Directories.Count);
            for (int i = 0; i < (int)count; i++)
            {
                int pos = i * entrySize;
                int tag = (int)Number(entries.AsSpan(pos), 2), type = (int)Number(entries.AsSpan(pos + 2), 2);
                long elements = checked((long)Number(entries.AsSpan(pos + 4), BigTiff ? 8 : 4));
                int size = Size(type); if (size == 0) continue;
                int vp = pos + (BigTiff ? 12 : 8);
                long length = checked(elements * size);
                byte[]? inline = length <= pointerSize ? entries.AsSpan(vp, (int)length).ToArray() : null;
                long offset = inline is null ? checked((long)Number(entries.AsSpan(vp), pointerSize)) : 0;
                dir.Fields[tag] = new(type, elements, offset, inline);
            }
            Directories.Add(dir);
            next = checked((long)Number(entries.AsSpan((int)count * entrySize), pointerSize));
        }
        if (Directories.Count == 0) throw Bad("No TIFF images found.");
    }
    public async Task<long> Value(TiffDirectory dir, int tag, long index = 0, long? fallback = null)
    {
        if (!dir.Fields.TryGetValue(tag, out var field)) return fallback ?? throw Bad($"TIFF tag {tag} is missing.");
        if (index < 0 || index >= field.Count) throw Bad("TIFF array index is outside the directory.");
        int size = Size(field.Type);
        // Offset/count arrays are deliberately NOT loaded in full.
        var bytes = field.Inline ?? await Read(checked(field.Offset + index * size), size);
        return checked((long)Number(bytes.AsSpan(field.Inline is null ? 0 : checked((int)index * size)), size));
    }
    public async Task<byte[]> Bytes(TiffDirectory dir, int tag, int max = 1024 * 1024)
    {
        if (!dir.Fields.TryGetValue(tag, out var f)) return [];
        long n = checked(f.Count * Size(f.Type));
        if (n <= 0 || n > max) throw Bad($"TIFF tag {tag} exceeds the metadata limit.");
        return f.Inline ?? await Read(f.Offset, (int)n);
    }
    public async Task<string> Description(TiffDirectory dir) => Encoding.UTF8.GetString(await Bytes(dir, 270)).TrimEnd('\0');
}
public sealed record TiffField(int Type, long Count, long Offset, byte[]? Inline);
public sealed class TiffDirectory(int index)
{
    public int Index { get; } = index;
    public Dictionary<int, TiffField> Fields { get; } = [];
    public long Width { get; set; }
    public long Height { get; set; }
    public int TileWidth { get; set; }
    public int TileHeight { get; set; }
    public byte[] JpegTables { get; set; } = [];
}

