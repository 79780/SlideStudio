using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SlideStudio.Services;
using Microsoft.Extensions.Configuration;

var root = args.Length > 0 ? args[0] : "http://127.0.0.1:5189";
using var client = new HttpClient { BaseAddress = new Uri(root), Timeout = TimeSpan.FromMinutes(2) };
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
async Task<JsonElement> Json(string path) { using var r = await client.GetAsync(path); r.EnsureSuccessStatusCode(); return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone(); }
int rendered = 0; long fetched = 0;
foreach (var slide in new[] { "Leica-1.scn", "Leica-2.scn" })
{
    var p = "/api/slides/" + slide;
    var info = await Json(p + "/info");
    Check(info.GetProperty("properties").GetProperty("storage").GetString() == "HTTP Range", "Wrong storage implementation");
    var notesBefore = (await Json(p + "/annotations")).GetRawText();
    var regions = slide == "Leica-1.scn" ? new[] { (10778.682, 35096.626, 36832.0, 38432.0) } : new[] {
        (41268.672,74571.912,39168.0,26048.0), (41483.16,50144.696,39360.0,23360.0),
        (36101.22,138532.72,39360.0,23360.0), (35886.728,163373.932,39168.0,26048.0)
    };
    foreach (var l in info.GetProperty("levels").EnumerateArray())
    {
        int level = l.GetProperty("level").GetInt32(); double ds = l.GetProperty("downsample").GetDouble();
        foreach (var (rx, ry, rw, rh) in regions)
        {
            int tx = (int)((rx + rw * .5) / ds / 256), ty = (int)((ry + rh * .5) / ds / 256);
            var plan = await Json(p + $"/tile-plan/{level}/{tx}/{ty}");
            var stored = plan.GetProperty("plan").GetProperty("tiles"); Check(stored.GetArrayLength() > 0, "Missing tissue ranges");
            using var response = await client.GetAsync(p + $"/tiles/{level}/{tx}/{ty}"); response.EnsureSuccessStatusCode();
            Check(response.Headers.GetValues("X-Decoder").Single() == "TIFF-Range-ImageSharp", "Wrong decoder");
            long bytes = long.Parse(response.Headers.GetValues("X-Source-Bytes").Single()); fetched += bytes;
            Check(bytes < 8 * 1024 * 1024, "Unexpected full-slide transfer");
            using var tile = Image.Load<Rgb24>(await response.Content.ReadAsByteArrayAsync());
            int tissuePixels = 0; foreach (var pixel in Enumerable.Range(0, tile.Height).SelectMany(y => Enumerable.Range(0, tile.Width).Select(x => tile[x,y]))) if (pixel.R > pixel.G + 8 && pixel.B > pixel.G + 3) tissuePixels++;
            Check(tissuePixels > 10, $"No tissue rendered in {slide} level {level}");
            using var again = await client.GetAsync(p + $"/tiles/{level}/{tx}/{ty}");
            Check(again.Headers.GetValues("X-Tile-Cache").Single() == "HIT", "Output cache miss");
            Check(again.Headers.GetValues("X-Source-Bytes").Single() == "0", "Cached tile performed range reads");
            if (level == 0)
            {
                // Independently compare decoded raw stored-tile pixels with composed output pixels.
                double error = 0; int samples = 0;
                foreach (var t in stored.EnumerateArray())
                {
                    long offset = t.GetProperty("offset").GetInt64(); int count = t.GetProperty("count").GetInt32();
                    using var request = new HttpRequestMessage(HttpMethod.Get, "/blob/" + slide); request.Headers.Range = new RangeHeaderValue(offset, offset + count - 1);
                    using var raw = await client.SendAsync(request); Check(raw.StatusCode == HttpStatusCode.PartialContent, "Range did not return 206");
                    using var sourceTile = Image.Load<Rgb24>(await raw.Content.ReadAsByteArrayAsync());
                    long col = t.GetProperty("column").GetInt64(), row = t.GetProperty("row").GetInt64();
                    for(int y=16;y<tile.Height-16;y+=8) for(int x=16;x<tile.Width-16;x+=8)
                    {
                        long sx=(long)Math.Floor(tx*256+x+.5-rx),sy=(long)Math.Floor(ty*256+y+.5-ry);
                        if(sx<0||sy<0||sx>=rw||sy>=rh||sx/512!=col||sy/512!=row)continue;
                        var a=tile[x,y];var b=sourceTile[(int)(sx%512),(int)(sy%512)];error+=Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B);samples+=3;
                    }
                }
                Check(samples>0 && error/samples<15, $"Pixel placement mismatch: {error/Math.Max(1,samples)}");
            }
            rendered++;
        }
    }
    // Compose actual viewer tiles into a reviewable overview, without any full-slide image read.
    var overview = info.GetProperty("levels")[Math.Min(4, info.GetProperty("levelCount").GetInt32()-1)];
    int ol=overview.GetProperty("level").GetInt32(),ow=overview.GetProperty("width").GetInt32(),oh=overview.GetProperty("height").GetInt32();
    using var montage=new Image<Rgb24>(ow,oh);
    for(int y=0;y<oh;y+=256)for(int x=0;x<ow;x+=256)
    {
        using var tile=Image.Load<Rgb24>(await client.GetByteArrayAsync(p+$"/tiles/{ol}/{x/256}/{y/256}"));
        for(int yy=0;yy<tile.Height;yy++)for(int xx=0;xx<tile.Width;xx++)montage[x+xx,y+yy]=tile[xx,yy];
    }
    await montage.SaveAsPngAsync(Path.Combine(AppContext.BaseDirectory, slide+".overview.png"));
    Check(notesBefore==(await Json(p+"/annotations")).GetRawText(),"Annotations changed");
    using var invalid=await client.GetAsync(p+"/tiles/999/0/0");Check(invalid.StatusCode==HttpStatusCode.BadRequest,"Invalid tile accepted");
    using var all=await client.GetAsync("/blob/"+slide);Check(all.StatusCode==HttpStatusCode.RequestedRangeNotSatisfiable,"Full blob GET allowed");
    using var badRequest=new HttpRequestMessage(HttpMethod.Get,"/blob/"+slide);badRequest.Headers.Range=new RangeHeaderValue(0,15);badRequest.Headers.IfMatch.Add(new EntityTagHeaderValue("\"stale\""));
    using var stale=await client.SendAsync(badRequest);Check(stale.StatusCode==HttpStatusCode.PreconditionFailed,"Stale blob accepted");
    using var beyond=await client.GetAsync(p+"/bytes?offset=0&count=1048577");Check(beyond.StatusCode==HttpStatusCode.BadRequest,"Oversized range accepted");
    Console.WriteLine($"PASS {slide}: every tissue region / every level; pixel placement; overview; cache; ETag; bounds; annotations preserved.");
}
// Validate reader refuses servers which ignore Range instead of consuming a full file response.
using(var source=new HttpRangeBlobSource(new FakeFactory(),new ConfigurationBuilder().Build()))
{
    try{await source.ReadAsync(new("test.scn",100,"\"v1\""),0,16,new(),default);throw new Exception("Full download accepted");}
    catch(SlideApiException e){Check(e.StatusCode==502,"Wrong full-download rejection");}
}
// Parser fixtures exercise both byte orders, classic TIFF, BigTIFF, inline values, and cycles.
foreach(bool big in new[]{false,true})foreach(bool little in new[]{false,true})
{
    var bytes=new byte[96];bytes[0]=bytes[1]=(byte)(little?73:77);
    void Put(int p,ulong n,int size){for(int i=0;i<size;i++)bytes[p+(little?i:size-1-i)]=(byte)(n>>(8*i));}
    Put(2,big?43UL:42UL,2);int start=big?16:8,prefix=big?8:2,entry=big?20:12,pointer=big?8:4;
    if(big){Put(4,8,2);Put(8,16,8);}else Put(4,8,4);
    Put(start,1,prefix);int at=start+prefix;Put(at,256,2);Put(at+2,4,2);Put(at+4,1,big?8:4);Put(at+(big?12:8),12345,4);
    var blob=new BlobReference("fixture.tif",bytes.Length,"v1");var parser=new TiffRangeReader(new MemorySource(bytes),blob,new(),default);await parser.Parse();Check(await parser.Value(parser.Directories[0],256)==12345,"Endian/inline parse failed");
    Put(at+entry,(ulong)start,pointer);
    try{await new TiffRangeReader(new MemorySource(bytes),blob,new(),default).Parse();throw new Exception("Cyclic IFD accepted");}catch(SlideApiException){}
}
Console.WriteLine($"PASS {rendered} tissue tile checks; {fetched:N0} source bytes during checked rendering. Parser fixtures and full-response rejection passed.");
sealed class FakeFactory : IHttpClientFactory {public HttpClient CreateClient(string name)=>new(new FullHandler());}
sealed class FullHandler : HttpMessageHandler {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(new byte[100])});}
sealed class MemorySource(byte[] bytes) : IRangeBlobSource
{
 public Task<BlobReference> DescribeAsync(string n,CancellationToken ct)=>Task.FromResult(new BlobReference(n,bytes.Length,"v1"));
 public Task<byte[]> ReadAsync(BlobReference b,long o,int n,ReadTrace t,CancellationToken ct)=>Task.FromResult(bytes.AsSpan((int)o,n).ToArray());
}
