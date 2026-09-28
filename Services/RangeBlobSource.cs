using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Caching.Memory;

namespace SlideStudio.Services;

public sealed record BlobReference(string Name, long Length, string ETag);
public sealed record RangeRead(long Offset, int Count, bool CacheHit);
public sealed class ReadTrace
{
    public List<RangeRead> Reads { get; } = [];
    public long TransferredBytes => Reads.Where(r => !r.CacheHit).Sum(r => (long)r.Count);
    public int Requests => Reads.Count(r => !r.CacheHit);
}

// The decoder has no filesystem dependency. This is the only storage contract it receives.
public interface IRangeBlobSource
{
    Task<BlobReference> DescribeAsync(string name, CancellationToken ct);
    Task<byte[]> ReadAsync(BlobReference blob, long offset, int count, ReadTrace trace, CancellationToken ct);
}

public sealed class HttpRangeBlobSource(IHttpClientFactory clients, IConfiguration config) : IRangeBlobSource, IDisposable
{
    public const int MaxRange = 1024 * 1024;
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 32 * 1024 * 1024 });
    private Uri Url(string name) => new(new Uri((config["BlobBaseUrl"] ?? "http://127.0.0.1:5188/blob/").TrimEnd('/') + "/"), Uri.EscapeDataString(name));

    public async Task<BlobReference> DescribeAsync(string name, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, Url(name));
        using var response = await clients.CreateClient("range").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new SlideApiException(404, "Slide blob not found.");
        response.EnsureSuccessStatusCode();
        var length = response.Content.Headers.ContentLength;
        var etag = response.Headers.ETag;
        if (length is null or < 16 || etag is null || etag.IsWeak) throw new SlideApiException(422, "Blob source must provide length and a strong ETag.");
        return new(name, length.Value, etag.ToString());
    }

    public async Task<byte[]> ReadAsync(BlobReference blob, long offset, int count, ReadTrace trace, CancellationToken ct)
    {
        if (offset < 0 || count <= 0 || count > MaxRange || offset > blob.Length - count)
            throw new SlideApiException(422, "Invalid or oversized range in slide metadata.");
        var key = $"{blob.Name}:{blob.ETag}:{offset}:{count}";
        if (cache.TryGetValue(key, out byte[]? hit)) { trace.Reads.Add(new(offset, count, true)); return hit!; }
        using var request = new HttpRequestMessage(HttpMethod.Get, Url(blob.Name));
        request.Headers.Range = new RangeHeaderValue(offset, offset + count - 1);
        request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(blob.ETag));
        using var response = await clients.CreateClient("range").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.PreconditionFailed) throw new SlideApiException(409, "Slide changed during reading. Reopen it.");
        var range = response.Content.Headers.ContentRange;
        if (response.StatusCode != HttpStatusCode.PartialContent || range?.From != offset || range.To != offset + count - 1 || range.Length != blob.Length || response.Content.Headers.ContentLength != count || response.Headers.ETag?.ToString() != blob.ETag)
            throw new SlideApiException(502, "Blob source did not return the exact requested 206 byte range and version. Full downloads are refused.");
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        var bytes = new byte[count]; await body.ReadExactlyAsync(bytes, ct);
        trace.Reads.Add(new(offset, count, false));
        cache.Set(key, bytes, new MemoryCacheEntryOptions().SetSize(count).SetSlidingExpiration(TimeSpan.FromMinutes(10)));
        return bytes;
    }
    public void Dispose() => cache.Dispose();
}

