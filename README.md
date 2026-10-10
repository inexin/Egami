# Egami

Egami ("image" backwards) is an open source 2D imaging library for .NET that loads, edits and saves images.

- **MIT-licensed**, with no strings for commercial use.
- **Fully managed C#**: no native binaries, no P/Invoke.
- **Safe on untrusted files**: size limits are checked before allocating, and readers are fuzz-tested.
- **Trimming and Native AOT clean**, on every OS and CPU .NET 10 supports.

> **Status:** early development. Nothing is published to NuGet yet. Phase 1.1 provides the core types and a complete static PNG reader and writer; Phase 1.2 adds resize, geometry, color, filter and compositing operations; Phase 1.3 adds JPEG reading and writing, typed EXIF and `AutoOrient`; Phase 1.4 adds animation: GIF, APNG and editable frames;
Phase 1.5 adds BMP, TGA, QOI and the Netpbm formats, and the 1.0 API. Full documentation is in [docs/](docs/index.md).

## Quick start

```csharp
using Egami;
using Egami.Pixels;

// Load in the pixel type that best fits the file (for example Rgb8 or Gray16)...
using Raster image = Raster.Load("photo.png");
Console.WriteLine($"{image.Width} × {image.Height}, {image.Layout.ChannelOrder}");

// ...or decode straight into the type you want.
using Raster<Rgba8> rgba = Raster.Load<Rgba8>("photo.png");

// Pixels are accessed a row at a time.
for (int y = 0; y < rgba.Height; y++)
{
    Span<Rgba8> row = rgba.Row(y);
    for (int x = 0; x < row.Length; x++)
    {
        row[x].A = 255;
    }
}

// The format is chosen from the extension.
rgba.Save("opaque.png");
```

Every raster owns its pixel memory, so dispose it when you are done. Methods that return a new raster say so in their name
(`Copy`, `As<TPixel>`, `Derive`); everything else works in place.

### Reading a header only

`Raster.Probe` reads the size, pixel layout, EXIF orientation and an estimate of the memory a full load needs, without
decoding pixels, so a server can reject an image before loading it.

```csharp
RasterInfo info = Raster.Probe(stream);
bool tooLarge = info.EstimatedBytes > 100_000_000;
bool sideways = info.Orientation is >= ImageOrientation.Transpose;   // shown with width and height swapped
```

### Limits for untrusted files

`EgamiContext` holds every setting that is not specific to one call. It is immutable; change it with `with`.

```csharp
EgamiContext context = EgamiContext.Default with
{
    Limits = SafetyLimits.Default with { MaxPixels = 50_000_000 },
    MaxParallelism = 4,
};

using Raster image = await Raster.LoadAsync(upload, context, cancellationToken: token);
```

A breached limit throws `LimitExceededException` before any pixel memory is allocated. Damaged data throws
`CorruptDataException` with the byte offset, and an unknown format throws `UnsupportedFormatException`; all three derive from
`EgamiException`. By default a reader recovers what it can from a truncated file and lists the problem in
`image.Metadata.Warnings`; pass `new ReaderOptions { Recovery = RecoveryMode.Strict }` to fail instead.

### Writing PNG

```csharp
using Egami.Formats.Png;

image.Save(stream, PngFormat.Info, new PngWriterOptions
{
    Reduce = true,            // smallest exact form: drops alpha, uses gray or a palette when nothing is lost
    CompressionLevel = 9,     // 0 (fastest) to 9 (smallest); 5 by default
});
```

### JPEG

JPEG files load like any other: baseline and progressive, gray, YCbCr, RGB, CMYK and YCCK, with any chroma subsampling. The
output matches libjpeg-turbo's `djpeg` exactly. Setting a target size decodes at 1/2, 1/4 or 1/8 scale straight from the
DCT, the smallest that still covers the target, which is far faster than decoding in full and resizing.

```csharp
using Egami.Formats;
using Egami.Formats.Jpeg;

// About 500 × 375 from a 4000 × 3000 photo, decoded at 1/8 scale.
using Raster preview = Raster.Load("photo.jpg", options: new ReaderOptions { MinimumSize = new Size(500, 375) });

image.Save(stream, JpegFormat.Info, new JpegWriterOptions
{
    Quality = 85,                            // 75 by default; the same scale as libjpeg's cjpeg
    Subsampling = ChromaSubsampling.Subsample444,    // 4:4:4; 4:2:0 by default
    OptimizeHuffman = true,                  // a few percent smaller, a little slower
});
```

`JpegWriterOptions.Background` composites rasters with alpha onto a color first; without it alpha is ignored. Facts about the
file (progressive, sampling, an estimate of the quality it was saved at) are in `image.Metadata.FormatDetails` as
`JpegDetails`.

### EXIF

`image.Metadata.Exif` is the parsed EXIF block from a JPEG or PNG file, or null. Common values have typed properties, any tag
can be read with `TryGet`, and a block that is not edited is saved back byte for byte.

```csharp
using Egami.Metadata;

if (image.Metadata.Exif is ExifData exif)
{
    Console.WriteLine($"{exif.CameraMake} {exif.CameraModel}, {exif.DateTaken}, {exif.Location}");
    if (exif.TryGet(ExifKey.ExposureTime, out ExifEntry? exposure))
    {
        Console.WriteLine($"1/{1 / exposure.AsDouble():0} s");
    }

    exif.Set(ExifKey.Copyright, "© 2026 Example");
    exif.Remove(ExifKey.GpsLatitude);
}
```

A damaged block keeps its raw bytes and sets `IsDamaged`; the pixels still load. Operations keep the block honest: rotating
and flipping reset the orientation, resizing and cropping update the pixel dimensions and drop the embedded thumbnail.

### Thumbnails

`Load` returns pixels as stored; `AutoOrient` turns them the way the camera meant and resets the orientation tag. Followed by a
resize, it runs on the small result, so the whole chain below decodes at 1/8 scale and turns 256 pixels, not 12 megapixels.

```csharp
using Raster thumb = Raster.Load("photo.jpg", options: new ReaderOptions { MinimumSize = new Size(256, 256) });
thumb.Apply(ops => ops.AutoOrient().Fit(256, 256));
thumb.Save("thumb.jpg", new JpegWriterOptions { Quality = 80 });
```

### Animations

A raster holds one or more frames, all the size of the raster. GIF and APNG files load with every frame composited to full
size, as a browser shows them; the frame list can be edited, and saving a raster with more than one frame as GIF or PNG writes
an animation.

```csharp
using Egami.Formats.Gif;
using Egami.Metadata;

using Raster<Rgba8> anim = Raster.Load<Rgba8>("clip.gif");
Console.WriteLine($"{anim.FrameCount} frames, plays {anim.Metadata.LoopCount} times (0 = forever)");

anim.Frames.RemoveAt(0);                                   // frames are copied in, moved and removed
anim.Frames.Add(anim.Frames[0]).Metadata.Delay = TimeSpan.FromMilliseconds(500);
anim.Frames.Move(anim.FrameCount - 1, 0);
anim.Apply(ops => ops.Fit(320, 320));                      // operations run on every frame

anim.Save("clip-small.gif");                               // or .png for an APNG
using Raster<Rgba8> still = anim.CopyFrame(0);             // one frame as its own raster

// Only the first frame, decoding nothing after it.
using Raster first = Raster.Load("clip.gif", options: new ReaderOptions { Frames = ..1 });
```

Writers work out each frame's stored rectangle, disposal and blend from the pixels, so edited animations stay small.

### Writing GIF

GIF holds at most 256 colors per frame. Images with few enough colors keep them exactly; others get a palette from Wu's
quantizer, with Floyd–Steinberg dithering by default.

```csharp
using Egami.Quantization;

photo.Save(stream, GifFormat.Info, new GifWriterOptions
{
    Palette = new PaletteOptions { MaxColors = 128, Dither = DitherMode.Ordered },   // FloydSteinberg, Ordered or None
    PaletteScope = GifPaletteScope.PerFrame,   // Auto (default): one table when all frames fit, else one per frame
});
```

The same `PaletteOptions` build a palette for `PngWriterOptions { ColorType = PngColorType.Palette }`, keeping partial alpha.

### Operations

Record a chain in a lambda: `Apply` changes the raster in place, `Derive` returns a new one and leaves the source untouched.
Egami plans the chain first, so a bad argument throws before any pixel changes, a `Crop` before a resize reads only the crop,
and adjacent color steps run as one pass. Each step runs in parallel bands of rows.

```csharp
using Egami.Operations;

// A 256 × 256 thumbnail that covers the box, cropped around the center; the source is not touched.
using Raster<Rgba8> thumb = photo.Derive(ops => ops.Cover(256, 256));

// In place: shrink to fit 1600 × 1600 (never enlarging), straighten, and brighten a little.
photo.Apply(ops => ops
    .Fit(1600, 1600, enlarge: false)
    .Rotate(-2.5f)
    .Brightness(1.05f)
    .Contrast(1.1f));

// 800 pixels wide in linear light, sharpened, with a watermark at 80% opacity.
photo.Apply(ops => ops
    .Resize(800, 0, ResampleFilter.Lanczos3, ColorSpace.Linear)
    .Sharpen(amount: 0.6f, sigma: 1)
    .DrawImage(logo, new Point(20, 20), opacity: 0.8f));
```

| Group | Operations |
| --- | --- |
| Resize | `Resize` (0 for one side keeps the aspect ratio), `Fit`, `Cover`; filters Nearest, Box, Bilinear, Bicubic (default, Catmull-Rom), Mitchell, Lanczos3; gamma-encoded or linear light |
| Geometry | `Crop`, `Pad`, `FlipHorizontal`, `FlipVertical`, `Rotate` (exact for multiples of 90°, resampled otherwise), `AutoOrient`, `FillColor` |
| Color | `Brightness`, `Contrast`, `Saturation`, `HueRotate`, `Sepia`, `Grayscale`, `Invert`, `Opacity`, `ColorMatrix` (W3C Filter Effects definitions) |
| Filters | `Blur` (Gaussian), `BoxBlur`, `Sharpen` (unsharp mask), `Convolve` with any `ConvolutionKernel` and `EdgeMode` |
| Compose | `DrawImage` (source-over, any pixel type, whole images or an area, clipped to the overlap) |

Pass a `CancellationToken` to stop between bands of rows. A step that needs a new plane checks `SafetyLimits.MaxTotalBytes`
first. Your own operation implements `IRasterOperation` and joins a chain with `ops.Add(...)`.

### Pixel types

| Type | Channels | Typical use |
| --- | --- | --- |
| `Rgba8`, `Bgra8` | 8-bit color with alpha | Default working type, interop with Windows and GPU buffers |
| `Rgb8`, `Bgr8` | 8-bit color | Photos without alpha |
| `Argb8`, `Abgr8` | 8-bit color with alpha first | Interop with alpha-first buffers |
| `Alpha8` | Alpha only | Masks |
| `Gray8`, `Gray16`, `GrayAlpha8`, `GrayAlpha16` | Gray | Grayscale images |
| `Rgba16`, `Rgb16` | 16-bit color, with or without alpha | 16-bit PNG |
| `RgbaF32`, `RgbF32`, `GrayF32` | 32-bit float color or gray | High-precision processing |
| `RgbaF16` | 16-bit half-float color with alpha | HDR and GPU textures |

Convert between them with `raster.As<TPixel>()` for a whole raster, or `PixelConvert.Convert` for a span. Pixel values are
sRGB-encoded with straight alpha. Conversion to gray uses Rec. 709 luma in linear light.

### Formats

| Format | Read | Write |
| --- | --- | --- |
| PNG | All color types and bit depths, interlaced, transparency, metadata | All color types and bit depths, adaptive filtering, built palettes |
| JPEG | Baseline and progressive, 8-bit, any subsampling, CMYK and YCCK, scaled decode, EXIF, XMP, ICC | Baseline, quality 1 to 100, 4:4:4, 4:2:2, 4:2:0 or gray, optimized Huffman tables, EXIF, XMP, ICC |
| GIF | GIF87a and 89a, animations with every disposal, interlaced, transparency, loop count, comments, XMP, ICC | GIF89a, exact or quantized palettes (global or per frame), dithering, frame optimization |
| APNG | Every PNG color type and depth, all disposal and blend modes | Any raster with more than one frame, changed rectangles only |
| BMP | Every Windows and OS/2 2.x header, 1 to 32 bits, RLE4, RLE8, RLE24, bit fields, embedded PNG and JPEG, V5 ICC | 24-bit, 32-bit with alpha, 1, 2, 4 and 8-bit palettes, resolution, ICC |
| TGA | Color-mapped, true-color and gray, raw and RLE, all origins, TGA 2.0 footer and extension area | Gray, 24 and 32-bit, 8-bit palette, raw or RLE, TGA 2.0 footer |
| QOI | The whole format | 3 or 4 channels, byte-identical to the reference encoder |
| PBM, PGM, PPM, PAM | P1 to P7, plain and binary, any maxval | P1 to P7, plain and binary |

Custom formats plug in through `FormatInfo`, `IFormatReader` and `IFormatWriter`, registered with
`EgamiContext.Default.WithFormat(...)`.

## Building

Requires the .NET 10 SDK.

```sh
dotnet build Egami.slnx
dotnet test Egami.slnx
```

Benchmarks: `dotnet run -c Release --project benchmarks/Egami.Benchmarks -- --filter "*"`.

## Repository layout

| Path | Contents |
| --- | --- |
| `src/Egami/` | The library, the only shipped project |
| `tests/Egami.Tests/` | Unit, conformance, round-trip and SIMD equivalence tests (xUnit) |
| `tests/Egami.Fuzz/` | Fuzzing harness (SharpFuzz and AFL++) |
| `tests/Egami.AotSmoke/` | Console app published with Native AOT in CI |
| `tests/Egami.DocSamples/` | The code the docs show, compiled and run with the tests |
| `samples/` | One small console app per guide; `dotnet run --project samples/Formats` |
| `tests/Egami.OutsideTests/` | Plugins written with only the public API: a pixel type, a memory source, operations, formats |
| `tests/images/`, `tests/reference/` | PngSuite, the JPEG, GIF, APNG, BMP, TGA, QOI and PNM corpora and their reference decodes, with licenses |
| `docs/` | The documentation site (DocFX): guides, cookbook, API reference. Build it with `docfx docs/docfx.json` (output in `docs/_site`) |
| `benchmarks/Egami.Benchmarks/` | BenchmarkDotNet benchmarks |
| `tools/` | Scripts that regenerate references and the public API baseline, check the docs are complete, and the libpng checker used in CI |

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Every change is written from specifications and the libraries in [docs/references.md](docs/references.md), and every commit is signed off.

## License

[MIT](LICENSE)
