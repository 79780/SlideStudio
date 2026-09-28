using System.Text.Json;
using System.Globalization;
using Microsoft.Data.Sqlite;
using SlideStudio.Services;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://127.0.0.1:5188");
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 1024 * 1024);
builder.Services.AddSingleton<SlideFiles>();
builder.Services.AddHttpClient("range", client => client.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.None });
builder.Services.AddSingleton<IRangeBlobSource, HttpRangeBlobSource>();
builder.Services.AddSingleton<RangeSlideService>();
var app = builder.Build();
SQLitePCL.Batteries_V2.Init();
var data = Path.Combine(app.Environment.ContentRootPath, "data");
Directory.CreateDirectory(data);
SqliteConnection Db() { var c = new SqliteConnection($"Data Source={Path.Combine(data, "slides.db")}"); c.Open(); return c; }
using (var db = Db()) { using var cmd = db.CreateCommand(); cmd.CommandText = "CREATE TABLE IF NOT EXISTS slides(name TEXT PRIMARY KEY, metadata TEXT NOT NULL); CREATE TABLE IF NOT EXISTS annotations(name TEXT PRIMARY KEY, body TEXT NOT NULL);"; cmd.ExecuteNonQuery(); }
app.Use(async (ctx, next) => {
    try { await next(); }
    catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { }
    catch (SlideApiException e) { ctx.Response.StatusCode = e.StatusCode; await ctx.Response.WriteAsJsonAsync(new { error = e.Message }); }
    catch (Exception e) { app.Logger.LogError(e, "Request failed"); ctx.Response.StatusCode = 500; await ctx.Response.WriteAsJsonAsync(new { error = "Unable to process slide. See application log." }); }
});
app.UseDefaultFiles(); app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache" });

// Local blob emulator: the only image-reading code allowed to open local files.
app.MapMethods("/blob/{name}", ["GET", "HEAD"], async (string name, SlideFiles files, HttpContext ctx) => {
    var path = files.GetPath(name); var fi = new FileInfo(path);
    string etag = $"\"{fi.Length:x}-{fi.LastWriteTimeUtc.Ticks:x}\"";
    ctx.Response.Headers.ETag = etag; ctx.Response.Headers.AcceptRanges = "bytes";
    ctx.Response.ContentType = "application/octet-stream";
    if (ctx.Request.Headers.IfMatch.Count > 0 && ctx.Request.Headers.IfMatch.ToString() != etag) { ctx.Response.StatusCode = 412; return; }
    if (HttpMethods.IsHead(ctx.Request.Method)) { ctx.Response.ContentLength = fi.Length; return; }
    var raw = ctx.Request.Headers.Range.ToString();
    if (!System.Net.Http.Headers.RangeHeaderValue.TryParse(raw, out var range) || range.Unit != "bytes" || range.Ranges.Count != 1) { ctx.Response.StatusCode = 416; ctx.Response.Headers.ContentRange = $"bytes */{fi.Length}"; return; }
    var part = range.Ranges.Single(); long start = part.From ?? -1, end = part.To ?? -1;
    if (start < 0 || end < start || end >= fi.Length || end - start >= HttpRangeBlobSource.MaxRange) { ctx.Response.StatusCode = 416; ctx.Response.Headers.ContentRange = $"bytes */{fi.Length}"; return; }
    int count = (int)(end - start + 1);
    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.RandomAccess);
    fi.Refresh();
    if ($"\"{fi.Length:x}-{fi.LastWriteTimeUtc.Ticks:x}\"" != etag) { ctx.Response.StatusCode = 412; return; }
    stream.Seek(start, SeekOrigin.Begin); var bytes = new byte[count]; await stream.ReadExactlyAsync(bytes, ctx.RequestAborted);
    ctx.Response.StatusCode = 206; ctx.Response.ContentLength = count; ctx.Response.Headers.ContentRange = $"bytes {start}-{end}/{fi.Length}";
    await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
});
app.MapGet("/api/slides", (SlideFiles files) => Directory.EnumerateFiles(files.Root).Where(p => new[] { ".scn", ".svs", ".tif", ".tiff" }.Contains(Path.GetExtension(p).ToLowerInvariant())).Select(p => new { name = Path.GetFileName(p), bytes = new FileInfo(p).Length }));
app.MapGet("/api/slides/{name}/info", async (string name, RangeSlideService slides, HttpContext ctx) => {
    var info = await slides.GetInfo(name, ctx.RequestAborted);
    using var db = Db(); using var cmd = db.CreateCommand();
    cmd.CommandText = "INSERT INTO slides VALUES($n,$m) ON CONFLICT(name) DO UPDATE SET metadata=$m";
    cmd.Parameters.AddWithValue("$n", name); cmd.Parameters.AddWithValue("$m", JsonSerializer.Serialize(info)); cmd.ExecuteNonQuery();
    return Results.Ok(info);
});
app.MapGet("/api/slides/{name}/tile-plan/{level:int}/{x:int}/{y:int}", async (string name, int level, int x, int y, RangeSlideService slides, HttpContext ctx) => Results.Ok(await slides.GetPlan(name, level, x, y, ctx.RequestAborted)));
app.MapGet("/api/slides/{name}/tiles/{level:int}/{x:int}/{y:int}", async (string name, int level, int x, int y, RangeSlideService slides, HttpContext ctx) => {
    var (tile, hit) = await slides.Render(name, level, x, y, ctx.RequestAborted);
    ctx.Response.Headers["X-Tile-Cache"] = hit ? "HIT" : "MISS";
    ctx.Response.Headers["X-Decoder"] = "TIFF-Range-ImageSharp";
    ctx.Response.Headers["X-Source-Bytes"] = tile.Trace.TransferredBytes.ToString(CultureInfo.InvariantCulture);
    ctx.Response.Headers["X-Range-Requests"] = tile.Trace.Requests.ToString(CultureInfo.InvariantCulture);
    ctx.Response.Headers["X-Stored-Tiles"] = tile.Plan.Tiles.Count.ToString(CultureInfo.InvariantCulture);
    return Results.File(tile.Jpeg, "image/jpeg");
});
app.MapGet("/api/slides/{name}/bytes", async (string name, long offset, int count, IRangeBlobSource source, HttpContext ctx) => {
    var blob = await source.DescribeAsync(name, ctx.RequestAborted);
    if (offset < 0 || count < 1 || count > HttpRangeBlobSource.MaxRange || offset > blob.Length - count) return Results.BadRequest(new { error = "Choose an in-file range of 1-1,048,576 bytes." });
    var bytes = await source.ReadAsync(blob, offset, count, new(), ctx.RequestAborted);
    ctx.Response.Headers.ContentRange = $"bytes {offset}-{offset + count - 1}/{blob.Length}";
    ctx.Response.Headers.AcceptRanges = "bytes"; ctx.Response.StatusCode = 206;
    return Results.Bytes(bytes, "application/octet-stream");
});
app.MapGet("/api/slides/{name}/annotations", (string name, SlideFiles files) => {
    files.GetPath(name); using var db = Db(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT body FROM annotations WHERE name=$n"; cmd.Parameters.AddWithValue("$n", name);
    return Results.Content(cmd.ExecuteScalar() as string ?? "[]", "application/json");
});
app.MapPut("/api/slides/{name}/annotations", async (string name, SlideFiles files, HttpContext ctx) => {
    files.GetPath(name); var notes = await ctx.Request.ReadFromJsonAsync<Annotation[]>();
    if (notes is null || notes.Length > 1000 || notes.Any(n => n is null || !double.IsFinite(n.X) || !double.IsFinite(n.Y) || !double.IsFinite(n.W) || !double.IsFinite(n.H) || n.X < 0 || n.Y < 0 || n.W <= 0 || n.H <= 0 || n.Label is null || n.Label.Length > 500)) return Results.BadRequest();
    using var db = Db(); using var cmd = db.CreateCommand(); cmd.CommandText = "INSERT INTO annotations VALUES($n,$b) ON CONFLICT(name) DO UPDATE SET body=$b";
    cmd.Parameters.AddWithValue("$n", name); cmd.Parameters.AddWithValue("$b", JsonSerializer.Serialize(notes, new JsonSerializerOptions(JsonSerializerDefaults.Web))); cmd.ExecuteNonQuery(); return Results.Ok(new { saved = true });
});
app.Run();
record Annotation(double X, double Y, double W, double H, string Label);
