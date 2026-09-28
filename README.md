# Slide Studio: HTTP byte-range viewer (no OpenSlide)

Double-click **Start-SlideStudio.cmd**, then open http://127.0.0.1:5188. The launcher uses the self-contained Windows x64 executable in **dist-range**. This replaces the earlier `dist` build, which is not used by the launcher. The new package contains no OpenSlide native library or bindings.

## How the image reaches the screen

```
Lab Images file
    -> /blob/{name} local blob emulator (HEAD + bounded HTTP Range GET)
    -> HttpRangeBlobSource (validates 206, Content-Range, ETag and length)
    -> TiffRangeReader (TIFF/BigTIFF directories and selected tile index entries)
    -> RangeSlideService (Leica layout, intersecting stored tiles, JPEG decoding)
    -> 256 x 256 JPEG viewer tiles
    -> OpenSeadragon 6.1.1 + SVG annotations
```

Only the local blob emulator opens image files. The parser and decoder have no file path or whole-slide stream: their only input is `IRangeBlobSource.DescribeAsync` and `ReadAsync(offset, count)`. In the default configuration, these methods perform real HTTP requests to the emulator on the same server. No original is uploaded, copied to a temporary file, or converted in full.

The reader obtains TIFF headers and image directories, parses Leica XML to place separate tissue regions, and reads individual `TileOffsets` / `TileByteCounts` entries. It fetches only intersecting compressed tiles. ImageSharp decodes those JPEG byte arrays; the renderer crops/composes a bounded viewer tile. Shared JPEG tables are joined to the tile when present. White background tiles do not need image-data ranges.

## Viewer

The frontend uses locally bundled **OpenSeadragon 6.1.1**. `wwwroot/tile-source.js` maps its small-to-large levels to the backend's native large-to-small levels using the actual downsample factors. No DZI files or preconversion are created. OpenSeadragon requests the existing JPEG tile API; all source reads remain in the range backend. The small navigator preview may request additional low-resolution tiles.

Zoom/pan/1:1/Fit and the navigator are handled by OpenSeadragon. SVG rectangles use its image/viewport coordinate transforms; annotation JSON and SQLite schema are unchanged. Press Escape to leave Rectangle mode. Requests use the blob version in the URL so browser tile caches cannot mix slide versions. The image loader is limited to four concurrent jobs, with a 160-tile cache target (visible tiles and rendering buffers can add memory).

- Select a slide, scroll to zoom, drag in Pan mode, or use Fit slide / 1:1.
- Rectangle mode adds an annotation using the label at right. Save annotations persists it in `data/slides.db`.
- Locate centers an annotation; Delete followed by Save removes it; Export JSON downloads original slide pixel coordinates.
- Tile grid displays viewer tile coordinates.
- **Inspect center tile ranges** shows the IFD, stored tile coordinates, offsets, lengths and total compressed bytes for the tile at the viewport center. Center the tissue of interest first. A background tile correctly reports no image ranges.
- The source counter reports bytes and range GETs for tiles returned in this viewing session. Cache hits may report zero. HEAD requests and earlier `/info` or inspector requests are not included.
- Read range displays a hex preview of up to 512 bytes from the selected range. Its maximum fetch is 1 MiB. This now uses the same HTTP range source as the decoder.

Annotations remain in original level-0 slide coordinates and the existing SQLite file is preserved. The slide's full dimensions are unchanged; lower-resolution geometry is calculated from the Leica regions. Pixel sampling and JPEG encoding can differ slightly from the former decoder.

## Memory, consistency and supported formats

- HTTP range reads: at most 1 MiB each. Larger compressed tiles are fetched in bounded chunks.
- Compressed byte cache: 32 MiB; output JPEG tile cache: 64 MiB; metadata cache: two slide versions.
- One viewer tile is rendered at a time. A decoded stored tile is at most 4 megapixels, compressed size at most 16 MiB, and output at most 256 x 256. Additional runtime/metadata/working-buffer memory is outside cache budgets.
- Every source request is tied to a strong ETag. The reader refuses a server that ignores Range and responds with the complete file. The emulator refuses full GETs, open-ended ranges, multiple ranges, ranges crossing EOF, and requests over 1 MiB.
- Supports the two supplied brightfield Leica SCN files, both containing tiled JPEG-compressed BigTIFF images, including all four tissue regions in Leica-2.
- Also implements a basic classic TIFF/BigTIFF tiled JPEG pyramid path, including big-endian parsing, for compatible TIFF/SVS files. Vendor SVS variants have not been validated against real samples.
- This is not a universal WSI decoder: JPEG 2000, LZW, stripped pyramids, non-top-left orientations, planar/fluorescence images and inconsistent Leica sampling are rejected. TIFF SubIFD-only pyramids are not currently traversed. ICC color-profile transformations are not implemented.

## APIs

| API | Purpose |
| --- | --- |
| `GET /api/slides` | Local emulator catalog |
| `GET /api/slides/{name}/info` | Dimensions, levels, decoder, storage, blob ETag |
| `GET /api/slides/{name}/tiles/{level}/{x}/{y}` | Viewer JPEG tile |
| `GET /api/slides/{name}/tile-plan/{level}/{x}/{y}` | Exact underlying compressed tile ranges |
| `GET /api/slides/{name}/bytes?offset=16&count=64` | Read raw source bytes through HTTP ranges |
| `GET /api/slides/{name}/annotations` | Load annotations |
| `PUT /api/slides/{name}/annotations` | Save annotations |
| `HEAD /blob/{name}` | Emulator length and version |
| `GET /blob/{name}` with `Range: bytes=start-end` | Emulator: exact partial response |

Tile responses expose `X-Decoder: TIFF-Range-ImageSharp`, `X-Source-Bytes`, `X-Range-Requests`, `X-Stored-Tiles`, and `X-Tile-Cache`.

## Moving to real blob storage

`BlobBaseUrl` configures the HTTP source prefix (default `http://127.0.0.1:5188/blob/`). The configured source must provide HEAD, exact bounded 206 responses, content lengths, strong ETags and If-Match support. An unauthenticated compatible test server can be selected directly.

For private Azure/S3 storage, implement `IRangeBlobSource` using the provider SDK or add authenticated HTTP requests. Credentials, SAS URL resolution, cloud catalog/database references and deployment are intentionally not configured in this local emulator release. The tile parser/renderer does not need a local image after replacing the source and local catalog/reference validation.

## Build and verify

```
dotnet build SlideStudio.csproj
dotnet publish SlideStudio.csproj -c Release -r win-x64 --self-contained true -o dist-range
dotnet run --project verification/RangeVerification.csproj -- http://127.0.0.1:5188
node verification/verify-osd.cjs
```

Run verification while the app is running. It reads both supplied slides, checks every tissue region at every pyramid level, independently compares level-0 output against decoded source JPEG pixels, generates overview PNGs, verifies cache/ETag/range limits, and confirms saved annotations are unchanged. Additional fixtures test both TIFF byte orders, classic/BigTIFF directories, cycle rejection and a server ignoring Range. It does not write annotations or modify originals.

Core dependencies remain ASP.NET Core/.NET 8, ImageSharp 3.1.12, Microsoft.Data.Sqlite.Core 8.0.8 and SQLitePCLRaw.bundle_e_sqlite3 2.1.6. No OpenSlide or LibTIFF package is required.

OpenSeadragon verification uses the actual bundled library for level scaling, tile bounds, coordinate-to-tile mapping and 33 real API requests across both slides. It does not replace an interactive browser test. The current automation environment exposes no browser, so visual interaction QA remains unverified. OpenSeadragon license and pinned version information are in `wwwroot/vendor/openseadragon`.
