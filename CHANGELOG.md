# Changelog

All notable changes to Egami are listed here. Egami follows [Semantic Versioning](https://semver.org): from 1.0, public API
changes that break code come only in a new major version.

## Unreleased

Additive to the 1.0 API: nothing in it changes or goes away.

### Tone and color

- Tone steps with ImageMagick's formulas and values from 0 to 1: `Level` (`-level` and `+level` in one), `Gamma`,
  `SigmoidalContrast`, `Posterize` (with ordered or Floyd–Steinberg dithering), `Solarize`, `Threshold` (luminance, each channel,
  saturation or chroma, with any two colors), `Colorize`, `Tint` and `Duotone`; Skia's `HighContrast`; and
  `SimulateColorBlindness` from Machado, Oliveira and Fernandes (2009) with a severity. Each is within one level of ImageMagick 7
  at 8 bits and two at 16; thresholds are identical. Tone steps, color matrices and filters next to each other run as one pass,
  as exact lookup tables on 8- and 16-bit images.
- `ColorFilter` holds a color adjustment as a value, as Skia's color filters: every tone step and color matrix, `Curves` of
  `ToneCurve`s (monotone curves through points, tables, functions), 3D tables, `Then` and `Lerp`. `RasterOps.ApplyFilter` runs one;
  `Apply` runs one on your own pixels; `ToLut3D` samples one into a table.
- `Lut3D` with tetrahedral (the default), trilinear and nearest reading and a 1D shaper; `CubeFile` reads and writes .cube files
  (Adobe 1.0 and Resolve's 1D-plus-3D form), strictly and within `SafetyLimits`, and has a fuzz target; `Lut3D.FromHald`, `ToHald`
  and `Raster.HaldIdentity` round-trip Hald CLUT images. Trilinear tables equal ImageMagick's `-hald-clut` within a level.
- Adjustments that measure the image first: `AutoLevel`, `ContrastStretch`, `Normalize`, `AutoGamma`, `AutoWhiteBalance`,
  `Equalize` and `AutoThreshold` (Otsu, triangle, Kapur). Inside a chain they measure what the steps before them made; inside
  `Within` and `OnChannels`, only the scope; across an animation, all frames at once unless `perFrame`. Their black and white
  points, gammas and levels equal ImageMagick's. `Raster.MeasureAutoLevel`, `MeasureContrastStretch`, `MeasureAutoGamma`,
  `MeasureWhiteBalance`, `MeasureEqualize` and `FindThreshold` return the filter or level without changing anything.
- `WhiteBalance` by a picked neutral color or by color temperature and tint (Bradford adaptation to D65); `Clahe` (Zuiderveld's
  CLAHE on L*, within the closeness gate of ImageMagick's `-clahe`); `LocalContrast` (ImageMagick's `-local-contrast`, within a
  level); and `AdaptiveThreshold` (Bradley and Roth) for unevenly lit pages.

### TIFF

- `TiffFormat` reads classic TIFF and BigTIFF: strips and tiles, chunky and planar, 1 to 64-bit integer and float samples, in
  gray, palette, RGB, CMYK and other inks, YCbCr, CIELab, ICCLab, ITULab and LogLuv. Codecs: LZW, Deflate, PackBits, CCITT RLE,
  Group 3 and Group 4, JPEG and old-style JPEG, WebP, ThunderScan and SGILog, with both predictors and either fill order. Every
  file in the test corpus and in libtiff's sample images decodes as libtiff does. Damaged files and IFD loops throw
  `CorruptDataException`, within `SafetyLimits`.
- `Raster.Load` reads the first full-resolution page, or `TiffReaderOptions.Page`; a `MinimumSize` read takes a SubIFD or reduced
  page of exactly the right size instead of decoding full size. Rows stream for strip and tile files alike. Exif, GPS, IPTC, XMP,
  ICC and resolution come from the page's tags; tags Egami doesn't model are kept in `TiffDetails.OtherTags` and written back.
- `TiffWriterOptions` writes uncompressed, LZW, Deflate, PackBits, CCITT Group 3 and 4, JPEG (gray and RGB) and WebP, with
  predictors, strips or tiles, classic or BigTIFF (`Layout`), and `Reduce` for the smallest exact form (1-bit, gray, palette).
  Each pixel type is written at its own depth. A TIFF source's compression, predictor, layout and byte order are kept unless set;
  a lossy compression is kept only while the pixels are unchanged. `ParallelCompression` compresses strips and tiles on up to
  `MaxParallelism` threads, with the same bytes. Files are within 1% of the reference library's size at the same settings, and libtiff,
  ImageMagick and Pillow read them all. JPEG compression takes CMYK too, written as libtiff writes separated JPEG.

### WebP

- `WebpFormat` reads and writes WebP: lossless (VP8L), lossy (VP8) with or without alpha, and animated, with no native code.
  Lossless files decode to libwebp's pixels exactly; lossy files to libwebp's YUV planes sample for sample and to RGB within one
  level (`YuvRaster.Load` returns the planes); animations compose every frame as libwebp's animation decoder does. Every kind
  streams through `RasterDecoder`, incrementally too, and an animation holds one canvas whatever its frame count.
- `WebpWriterOptions`: `Lossless` (by default a WebP source keeps its kind and anything else is lossy), `Quality` and `Effort` with
  cwebp's meanings, `AlphaQuality`, `KeepTransparentColors`, `SharpYuv`, `NearLossless`, `MixedFrames` and `FirstFrameOnly`. At
  quality 75 and effort 4 lossy files are the reference library's size at the same quality, written twice as fast; lossless files are 4 to 9%
  smaller than the reference library's at every effort. Animations store changed rectangles; a GIF or APNG converts with Load and Save.
- `WebpReaderOptions.UseBackgroundColor` starts animations from the ANIM color, and `FastUpsampling` repeats chroma samples.
  `WebpDetails` and `WebpFrameDetails` say what a file held and keep unknown chunks (`WebpChunk`, `WebpChunkPosition`) for round
  trips. ICC, EXIF and XMP are read and written; the resolution travels in EXIF.

### JBIG2

- `Jbig2Format` reads JBIG2 (T.88): generic, refinement, text, symbol dictionary, pattern and halftone segments, arithmetic and
  Huffman coded, with MMR and custom code tables, from standalone files (sequential or random access) and from the embedded
  streams PDF holds, with `Jbig2ReaderOptions.Globals` (`Jbig2Globals.Parse`) and `Page`. Every page in the test corpus decodes
  bit for bit as PDFium and jbig2dec do, two to seventeen times faster than PdfPig. Pages load as `Gray8` and stream a stripe at
  a time; `Jbig2Details` says how a page was coded. Damaged regions are skipped with a warning; decoding stays within
  `SafetyLimits`.
- `Raster.Load`, `LoadAll`, `Probe` and `RasterDecoder.Open` take a `FormatInfo` to read data in a known format without
  detection, as PDF's JBIG2 streams need.

### JPEG 2000

- `Jpeg2000Format` reads all of JPEG 2000 Part 1 (T.800): JP2 and JPX files and raw codestreams, including a PDF's JPXDecode
  streams, which load with plain `Raster.Load`. Every progression order and progression change, every code-block style, tiles,
  tile-parts, precincts, packed headers, ROI, the reversible 5/3 and irreversible 9/7 wavelets, RCT and ICT, subsampled and
  signed components. Every ISO/IEC 15444-4 conformance codestream decodes within its tolerances: reversible ones identical to
  OpenJPEG 2.5.4, irreversible ones within one level, and the same at reduced resolutions.
- Palettes, channel definitions, sYCC and CIELab (converted to sRGB), ICC profiles (kept in the metadata), XMP and resolution are
  read; `Jpeg2000ReaderOptions.Components` returns the codestream's components as stored instead, for a PDF that names its own
  color space; `MaxQualityLayers` decodes the first layers only; `FastUpsampling` repeats subsampled chroma as OpenJPEG does.
  Subsampled sYCC also loads into a `YuvRaster`. `Jpeg2000Details` says how the codestream was coded.
- `MinimumSize` drops whole wavelet levels, `Region` decodes only the tiles and code-blocks it needs, and rows stream a row of
  tiles at a time. Tier 1 runs on every core; at one thread lossy files decode faster than OpenJPEG through Magick.NET, and at
  all cores every file decodes two to four times faster. Part 2 coding tools and High-Throughput code-blocks throw
  `NotSupportedException` naming the feature.

### Several images in one file

- `Raster.LoadAll` and `LoadAllAsync` return a `RasterList` of every image in a file, each at its own size and pixel type;
  single-image formats give a list of one. The list disposes its rasters; `Detach` keeps one. `RasterInfo.ImageCount` says how
  many there are without decoding.
- `Raster.SaveAll` and `SaveAllAsync` write several rasters as one multi-page TIFF; a format that holds one image takes one.

### Geometry

- `Transform` takes an affine `Matrix3x2`, a perspective `Homography` or a `Matrix4x4` projected as CSS `matrix3d` draws a flat
  element; `Skew` and `QuadDistort` build one. `Homography` is a double-precision 3 × 3 matrix with `FromQuad`, the usual factories,
  `TryInvert` and `Map`. `TransformOptions` chooses the filter, the color space, the edges (transparent, clamp, mirror, wrap), the
  background and a viewport; without one the result is the transformed bounding box.
- Where a transform shrinks or tilts the image, each pixel averages the ellipse its footprint covers (elliptical weighted
  averaging, capped at 32 source pixels), within 40 dB of ImageMagick's `-distort`; elsewhere it samples exactly as `Rotate` does.
  A pure scale equals `Resize` and a pure rotation equals `Rotate`, pixel for pixel.
- `Warp` runs any `IWarp` (or `Warp.FromFunction`). `Warps` holds ImageMagick's Barrel, BarrelInverse, Polar, DePolar, Arc, Swirl,
  Wave and Implode, with its parameters, whose points land where ImageMagick's do, and Skia's Magnifier. `Displace`, `Offset` and
  `Tile` move pixels by a map, by whole pixels, and in repeats.
- `Trim` and `EntropyCrop` crop to the content, `Deskew` straightens a page, and `Raster.TrimBounds`, `Raster.EntropyBounds` and
  `Raster.DetectSkew` measure without changing anything. Their size depends on the pixels: the chain runs up to them, measures every
  frame and plans the rest from the real size. Trim's rectangles equal ImageMagick's `-trim -fuzz` at 8 and 16 bits.
- `ResampleFilter` gains `Lanczos2`, `Hermite`, `Gaussian` and `Spline`, with ImageMagick's definitions, everywhere a filter is taken.
- `MipChain` makes many sizes from one image fast, each from the smallest halved level at least twice its size.
- `Raster.Append`, `Raster.Grid` and `SpriteSheet.Pack` build strips, contact sheets and packed sprite sheets.

### Planar YUV

- `YuvRaster<TSample>` holds a picture as Y, U and V planes and an optional alpha plane: 4:2:0, 4:2:2, 4:4:4 and 4:0:0, 8 bits
  in bytes, 10, 12 and 16 bits in `ushort`s. `YuvColor` states the matrix (`YuvMatrix`), range (`YuvRange`) and chroma siting
  (`ChromaSiting`), with presets and ITU-T H.273 codes (`YuvColor.TryFromH273`, `ColorCodePoints.TryGetYuvColor`). `Create` allocates the planes in one block from the memory source;
  `Wrap` takes a decoder's planes with strides, without a copy; `YuvPlane.AsGray8` views an 8-bit plane.
- `ToRaster`, `CopyTo`, `FromRaster` (with an alpha plane only when asked) and `CopyFrom` convert to and from every pixel type, within one level of the exact H.273
  result, with bilinear chroma by siting (or nearest and fast with `YuvConvertOptions`), in SIMD on every core. A repeated
  `CopyTo` or `CopyFrom` allocates nothing. 1080p 4:2:0 to `Rgba8` takes 0.5 ms on 24 cores.
- `CopyFromPacked` and `CopyToPacked` copy NV12, NV21, P010, P016, YUYV and UYVY frames (`YuvPackedFormat`).
- `YuvRaster.Load` and `Probe` give a JPEG's own planes, identical to libjpeg-turbo's raw output, also at reduced scale; saving
  JPEG-colored planes encodes them straight, with libjpeg-turbo's bytes. `Save` writes any other format through a raster.
- `Crop` (on chroma boundaries), `Resize` and `ResizeTo` (into an existing raster) work on the planes.

### Blending

- `BlendMode` names 31 modes: the twelve Porter–Duff operators, Plus, Modulate, the W3C separable and non-separable blend modes
  (Multiply to Luminosity), Add and Subtract. `DrawImage` takes a mode, an opacity and a color space (stored values by default,
  linear light as an option); only the overlap changes. `SourceOver` on stored values gives exactly what 1.0's `DrawImage` gives.
- On 8-bit rasters the Porter–Duff operators, Plus, Modulate and the product-and-sum modes are exact integer math, equal bit for
  bit to the W3C formulas rounded to nearest; ColorDodge, ColorBurn, SoftLight and the non-separable modes are within a level,
  with alpha exact. Every mode is within a level of SkiaSharp (two for Difference and Exclusion, where Skia rounds) and within
  two levels of the reference library's modes.
- `BlendColor` blends one color over every pixel (tint with Multiply, colorize with Color). `DrawArithmetic` draws with SVG's
  arithmetic composite, k1·S·D + k2·S + k3·D + k4.
- `Compositor.Blend` blends spans of any pixel type, with per-pixel coverage for antialiased edges or one color through coverage,
  for custom operations and rasterizers. It allocates nothing once warm and runs on many threads at once.
- `Compositor.Blend` also takes an `RgbaF32` color, kept at full precision on float and 16-bit rows. `Raster.Context` is public, so
  code outside Egami (a canvas) uses a raster's memory source and `MaxParallelism`.
- Fixed: a one-pixel straight `Rgba8` or `Bgra8` source row blended as if premultiplied (Multiply of a half-transparent gray gave
  178 where 139 is right).

### Egami.Drawing 1.0.0-preview.1

A new package, over Egami's public API only, that draws antialiased paths, shapes, strokes, clips and images on a canvas. A
preview: its API may change before 1.0. See the [Drawing guide](docs/articles/guides/drawing.md).

- `Canvas` is abstract: public calls are checked and tracked (transform, clip bounds, save stack) and each ends in one protected
  hook, so other backends (a PDF page, a recorder) see shapes and pens as data. `RasterCanvas<TPixel>` draws into any pixel type;
  `raster.CreateCanvas()` and the `Draw` operation step (per frame, inside `Within`) are the ways in.
- `Path` (immutable lines, quadratics and cubics with a `FillRule`) and `PathBuilder` with SVG endpoint arcs, tangent arcs, rectangles,
  round rectangles with per-corner radii (`RoundRect`, scaled as CSS does), ovals and polygons; `Contains`, `TightBounds`,
  `IsConvex`, `Reverse` and `Transform` by a matrix or a homography.
- Exact-area antialiasing: every pixel gets its covered area to 1/255, abutting shapes leave no seams, and results are identical at
  any core count. Fills are within a mean level of SkiaSharp and closer than SkiaSharp to the exact area.
- `Stroke` with butt, round and square caps, miter, round and bevel joins and a miter limit; width 0 is a one-pixel hairline.
- Antialiased clips by rectangle, round rectangle or path, intersecting or cutting out; pixel-aligned rectangles are free, and a
  fill through a mask costs about 1.16 times the plain fill.
- `DrawImage` under the transform and clip with Egami's samplers; at whole-pixel placement it equals `DrawImage` in a chain.
- Faster than the reference drawing library 2.1.7 in every scenario timed (ms, 1 thread): 1,000 small shapes 4.0 against 11.1, a 4000×4000
  star 19.8 against 49.4 (4.8 on 24 cores against 47.6), 10,000 icons 109 against 312, 200 thick curves 40 against 360, 10,000
  hairlines 48 against 208.

### Regions and masks (in progress)

- `Within(Rect, …)` runs any same-size chain so that only pixels inside the rectangle change. Filters read the real pixels
  around it, so a blurred rectangle has no seam; flips mirror the rectangle; custom operations work unchanged. Steps that change
  the size or the color encoding throw when the chain is recorded, naming the step.
- `Within(Mask, …)` mixes the chain's result with the original by a `Mask`'s coverage: any raster's gray value, alpha or
  luminance (`MaskChannel`), placed at an offset and optionally inverted. Coverage 0 and full are exact; 16-bit masks keep their
  gradient on 16-bit images. Scopes nest.
- `ExtractChannel` returns one channel (`ColorChannel`) as a gray raster and `CombineChannels` builds an image from gray rasters,
  both identical to ImageMagick's -separate and -combine at 8 and 16 bits. `ShuffleChannels` rearranges channels (`ChannelSource`)
  as one color matrix that fuses with the other color steps. `OnChannels` keeps a chain's result only in chosen channels
  (`ColorChannels`), as ImageMagick's -channel does.
- `AlphaToGray`, `LuminanceToAlpha`, `CopyAlpha` and `Flatten` match ImageMagick's -alpha extract, copy and remove; the JPEG
  writer's Background option now runs on Flatten's arithmetic, with unchanged bytes.

### Keying and arithmetic (in progress)

- `MakeTransparent`, `ReplaceColor` and `FloodFill` equal ImageMagick's -transparent, -opaque and -floodfill with -fuzz at 8 and
  16 bits; the tolerance is ImageMagick's color distance, and `MakeTransparent`'s softness ramps alpha for clean edges. `FloodFill`
  is four-connected by default (`Connectivity`). `ColorMask` and `FloodMask` return selections for `Within(Mask, …)`.
- `Arithmetic` applies a `PixelMath` operator with a constant, identical to ImageMagick's -evaluate at 8 and 16 bits.
  `Raster.Stack` combines several rasters by mean, median, min or max (`StackMode`) a few rows at a time.

### Streaming

- `RasterDecoder`: open an image and read it a band of rows (`ReadRows`) or a frame (`ReadFrame`) at a time, in any pixel
  type. `RasterInfo.Access` says whether a file streams or is decoded into a buffer first, and `WorkingBytes` what it holds.
- `ReaderOptions.Region` decodes only a rectangle, for every format, exactly as Load then Crop gives it.
- `Raster.LoadInto` decodes into a raster the caller already has.
- PNG, APNG, JPEG, GIF, BMP, TGA, PNM and QOI stream: `RasterDecoder` holds a few rows (or, for animations, one canvas) instead
  of the whole image. Interlaced PNG, progressive JPEG and bottom-up BMP and TGA on streams that cannot seek are buffered at the
  cheapest stage.
- `RasterDecoder.OpenIncremental`, `Append` and `EndOfInput` decode while bytes are still arriving; `Status` (`DecodeStatus`) and
  `HeaderRead` say how far it has got, and `RenderPreview` draws an interlaced PNG, a progressive JPEG or a GIF frame as far
  as it has arrived. Every 1.x format decodes as its bytes arrive, except files that are decoded into a buffer first.
- `LoadAsync` from a stream that cannot seek, such as an upload, decodes it in 256 KB chunks as it arrives instead of reading the
  whole file into memory first; results are the same. Seekable streams, plugins, progressive JPEG and files decoded into a buffer
  first keep 1.0's faster buffered path. `RasterDecoder.OpenAsync` with `ReadRowsAsync`, `ReadFrameAsync` and `NextFrameAsync`
  reads rows and frames from an asynchronous stream.
- `RasterEncoder.Create` with `WriteRows`, `WriteFrame` and `Complete` (and their async forms) writes an image a band of rows or
  a frame at a time, with the bytes `Save` writes. Options that need every pixel first throw `NotSupportedException` at
  `Create`. `PngWriterOptions.FrameCount` declares an APNG's frame count for streams that cannot seek. `SaveAsync` now writes
  each band out as it is encoded instead of holding the whole file.
- `Raster.Transcode` and `TranscodeAsync` decode, run a chain of streamable operations (crop, the resize family, horizontal flip,
  per-pixel color adjustments) and encode, a band of rows at a time. A leading resize shrinks on load and a leading crop
  becomes a region; other operations throw `NotSupportedException`. A 20,000 × 20,000 PNG to a 1,024-pixel JPEG peaks at
  0.4 MB of pixels.
- Plugins can stream: `IRowFormatReader` with `RowReader<TNatural>`, and `IRowFormatWriter` with `RowEncoder<TPixel>` and
  `EncodeStart<TPixel>`. Formats without them keep working through the buffered fallback.
- A 32-bit BMP whose alpha byte is zero everywhere now loads into 16-bit and premultiplied pixel types without a lossy round trip
  when the stream can seek.
- Behaviour change: `ReaderOptions.MinimumSize` now reduces every format, by the largest whole factor that keeps the image at
  least that big, averaging blocks of pixels. JPEG keeps its 1/2, 1/4 and 1/8 IDCT scaling and averages blocks beyond 1/8.
  The average is bit-identical to a full decode and a box resize, and a quarter-size decode is 1.2 to 2 times as fast as
  the reference library's `TargetSize` with the Box sampler.
- `ConvertColorTo` applies to streamed reads too, with the same HDR tone mapping as `ConvertColor`.

### Metadata

- IPTC: `IptcData`, `IptcEntry` and `IptcKey` read and write IPTC-IIM datasets, with typed getters for keywords, caption,
  headline, bylines, copyright, credit and the creation date. Text follows the coded character set (1:90), and Egami sets it to
  UTF-8 whenever a value is not ASCII; `Set` and `Add` enforce the IIM length caps. `RasterMetadata.Iptc` holds it, parsed on
  first use.
- XMP: `XmpData` parses a packet into properties by namespace URI and local name (`XmpName`, `XmpNamespaces`), each text, an
  `XmpArray` (bag, sequence or language alternatives) or an `XmpStruct`, with typed properties for the common Dublin Core,
  XMP Basic, Photoshop and TIFF values. `RasterMetadata.XmpData` is a live view of `RasterMetadata.Xmp`: an edit is what every
  format saves. DTDs are refused and nothing outside the packet is read.
- EXIF: the Windows XP tags (`ExifKey.XPTitle` and four more) and the encoded strings (`UserComment`, `GpsProcessingMethod`,
  `GpsAreaInformation`) read and write as text. `ExifData.ReadThumbnail` decodes the IFD1 thumbnail; `SetThumbnail` makes one
  from any raster.
- Behaviour change: JPEG keeps APP13 (`JpegDetails.PhotoshopResources`, with the IPTC) through load and save; 1.0 dropped it.
  After an IPTC edit the other Photoshop resources are written back unchanged and the IPTC digest is kept current.
- Behaviour change: PNG reads its XMP iTXt chunk and the legacy "Raw profile type" chunks (exif, APP1, iptc, xmp) into `Xmp`,
  `Exif` and `Iptc` instead of `Text`, and writes `Xmp` (1.0 ignored it) and `Iptc` (as "Raw profile type iptc").
- Behaviour change: flips, turns, rotation and auto-orient drop the IFD1 thumbnail, which would show sideways; crops and resizes
  already did.
- Behaviour change: `ExifEntry.AsString()` returns readable text for the XP tags and encoded strings instead of their raw bytes.
- Behaviour change: geometry operations move EXIF `SubjectArea` and `SubjectLocation` with the picture, and keep the XMP copies
  of the orientation, size and subject in step.

### Color management

- `ColorProfile.FromIcc` reads RGB, gray and CMYK ICC profiles, v2 and v4, matrix-shaper and table based; `ToIcc` writes
  v4.3 (v4.4 with cicp for HDR encodings). `IccData` and `IccTag` read any profile's header and tags.
- `ColorTransform` converts spans of any pixel types between two profiles under the four rendering intents, with black point
  compensation. It is compiled once and cached per `EgamiContext`: a matrix-shaper pair runs as curves, one matrix and curves
  with SIMD, anything else through a 33³ or 17⁴ grid. It matches Little CMS 2 within 0.0005 in float and one level in
  8 bits on 13,040 matrix transforms, and within ΔE2000 0.3 on table and CMYK ones; Display P3 to sRGB runs about 1.8× as
  fast as SkiaSharp.
- `RasterOps.ConvertColor`, `Raster.ConvertColor<TTo>` and `ReaderOptions.ConvertColorTo` convert images, the last while
  decoding, a band of rows at a time. `WriterOptions.OmitSrgbProfile` leaves out an embedded profile that is just sRGB.
- `RasterMetadata.CodePoints`, `Primaries` and `Hdr` (`HdrMetadata`) hold format-neutral color tags: PNG cICP, cHRM, gAMA,
  mDCV and cLLI and BMP endpoints are read into them and written from them. `GetColorProfile` resolves what the pixels mean.
- HDR: PQ and HLG convert to SDR with tone mapping (`ToneMapping.Bt2390` by default, `Reinhard`, `Clip`) and a hue-keeping
  gamut clip; `RasterOps.ToneMap` tone maps in place.
- Color values: `HslColor`, `HsvColor`, `XyzColor`, `LabColor` (with `DeltaE2000`), `LchColor`, `OklabColor`, `OklchColor`,
  `YCbCrColor` and `CmykColor`, all `IColorValue<TSelf>`; `ColorConvert` converts single values and whole rows (Lab, HSL,
  HSV and Y'CbCr rows run 1.1× to 11× as fast as the reference library). `YuvMatrix` and `YuvRange` name the Y'CbCr coefficients.
- Behaviour change: writers now emit the color tags held in `CodePoints`, `Primaries` and `Hdr` (PNG cICP, cHRM, mDCV, cLLI;
  BMP endpoints), which 1.0 left out.
- Behaviour change: operations that work in linear light (`ColorSpace.Linear`) now use the raster's own transfer curve, so a
  gamma 2.2 or Rec. 709 image is linearized with its curve rather than sRGB's.
- New enums: `RenderingIntent`, `IccProfileClass`, `ToneMapping`, `YuvMatrix`, `YuvRange`.

### Pixel types

- `Rgb16` and `GrayAlpha16`: 16-bit color without alpha and 16-bit gray with alpha, with exact conversions and SIMD to and
  from `Rgb8` and `GrayAlpha8`.
- `Argb8` and `Abgr8`: alpha-first byte orders, with SIMD byte shuffles to and from `Rgba8` and `Bgra8`.
- `RgbaF16`, `RgbF32` and `GrayF32`: half-float color, and float color and gray without alpha. Half conversions round as
  `(Half)f` does, with SIMD kernels identical to the casts for every one of the 2^32 float and 65,536 half values.
- `Bgr565`, `Bgra4444` and `Rgb10A2`: packed 16 and 32-bit color in the DXGI and Vulkan layouts. Widening and narrowing both
  round to nearest, so every route gives the same pixel and every packed value round-trips; SIMD kernels convert 5-6-5 to
  and from `Rgba8` and `Bgra8`.
- `Cmyk8` and `Cmyk16`: ink amounts, 0 meaning none, converted to and from RGB with the plain formula until color profiles
  arrive. `Raster.Load<Cmyk8>` of a CMYK or YCCK JPEG returns the file's inks without the RGB step. Resize, blur, sharpen,
  convolution and rotation filter the four inks, so black stays black; `DrawImage` blends inks; color adjustments go through
  RGB and rebuild black.
- `PremulRgba8`, `PremulBgra8` and `PremulRgbaF32`: color stored premultiplied by alpha. Conversions take and return straight
  colors (exact rounding both ways, SIMD for the 8-bit pairs, every stored pixel round-trips); resize, blur, sharpen,
  convolution, rotation and `DrawImage` run on the stored values directly. On translucent images resize is about 30% and
  `DrawImage` about 35% faster than on `Rgba8`.
- `Alpha8`: an alpha-only mask. It reads as black with that alpha; converting a color to it keeps only the alpha.

### Formats

- PFM float maps (PF and Pf) in the Netpbm family: both byte orders, loaded as `RgbF32` or `GrayF32` in linear light, written
  little-endian; `.pfm` saves as PFM, and `PnmDetails` gains `Scale` and `LittleEndian`. New enum value `PnmKind.FloatMap`.
- PNG and PNM read and write `Rgb16` and `GrayAlpha16` directly, and BMP reads 5-6-5 and 4-4-4-4 bit fields straight into
  `Bgr565` and `Bgra4444`, with no intermediate type. Natural types (what `Raster.Load` returns without a type) are unchanged.

### PNG writing

- `PngWriterOptions.Interlace` writes Adam7 interlaced PNG and APNG, for every color type, bit depth and palette. Off by default,
  and not taken from an interlaced source. A streamed encode holds the rows until the last one.
- `PngWriterOptions.ParallelCompression` compresses images with more than 4 MiB of image data in fixed 1 MiB bands on up to
  `MaxParallelism` threads. The bytes are the same at any thread count; files were 0.1% to 0.3% larger, and a 24-megapixel
  photo saved 8 times as fast on 12 cores. Off by default; with it off, or below the threshold, files are unchanged.

### JPEG reading

- Arithmetic-coded files (SOF9 and SOF10, sequential and progressive, with DAC conditioning) load like Huffman-coded ones, with
  the same pixels as libjpeg-turbo's djpeg, at every scale and when streamed. `JpegDetails.Arithmetic` says which a file is.
- 12-bit files (extended and progressive, Huffman or arithmetic) load in their natural 16-bit types, `Gray16`, `Rgb16` or
  `Cmyk16`, each sample scaled from 0..4095 to the full range; `Load<Rgba8>` and other 8-bit types round them. Every corpus file
  matches djpeg at 12 bits exactly, including at 1/2, 1/4 and 1/8 scale. `JpegDetails.Precision` gives the bits per sample.
- Lossless files (SOF3, and SOF11 with arithmetic coding) at 2 to 16 bits, every predictor and point transform, decode to
  exactly the samples djpeg gives (Thomas Richter's libjpeg for arithmetic coding, which libjpeg-turbo lacks): up to 8 bits as
  `Gray8` or `Rgb8`, deeper as `Gray16`, `Rgb16` or `Cmyk16`. They have no DCT, so a smaller size comes from the general
  reduction after a full decode. `JpegDetails.Lossless` says so. Hierarchical files are still refused.
- Large files decode on several cores, with the same pixels at every `MaxParallelism`. A sequential file with restart markers is
  split at the markers already in it; the scans of a progressive file run as a pipeline, each refinement scan following the
  scan before it row by row, and the last pass is split by rows. The 4000 × 3000 progressive benchmark photo decodes in half the
  time on 8 cores; the same photo with a marker every MCU row in under half. Files without markers decode as before.
- Faster arithmetic decoding than the reference library 3.1.12 at one thread and on all cores.

### JPEG writing

- `JpegWriterOptions.Progressive` writes progressive files with libjpeg's standard scan script (ten scans for color, six for
  gray) and Huffman tables optimized per scan. They decode to exactly the baseline file's pixels and are the same bytes as
  libjpeg-turbo's `cjpeg -progressive` at the same quality and sampling. The quantized coefficients are held until the last
  scan, about 2 bytes per sample, within `SafetyLimits.MaxTotalBytes`; a streamed encode buffers. Off by default, and a
  progressive source is re-saved as baseline unless it is set.
- `JpegColorType.Cmyk` and `JpegColorType.Ycck` write four components behind an Adobe marker, inverted as Photoshop writes CMYK;
  `Auto` picks CMYK for `Cmyk8` and `Cmyk16` pixels, which 1.x converted to RGB on save. YCCK subsamples its chroma and keeps
  black at full size. djpeg, ImageMagick and Pillow read every file to within a level of Egami's inks. 12 MP encodes faster than
  the reference library 3.1.12's CMYK and YCCK encoder at one thread (95 against 106 ms, 91 against 98) and on all cores (2.2 and 3.3 times
  as fast).
- `JpegWriterOptions.Precision = 12` writes 12-bit files (SOF1, or SOF2 when progressive) that keep the detail of 16-bit and
  float pixels, with optimized Huffman tables and 16-bit quantization tables where quality needs them. djpeg reads them within a
  level of Egami's decode; browsers, Pillow and many ImageMagick builds don't read 12-bit JPEG at all.
- TIFF's JPEG compression takes CMYK pages (see TIFF above).
- Arithmetic and lossless JPEG are still read only.

### Pixel layouts

- New enum values, for code that switches on them: `PixelChannelOrder.Argb`, `Abgr`, `Alpha` and `Cmyk`;
  `PixelChannelType.Float16` and `Packed`; `AlphaKind.Premultiplied`.
- `PixelLayoutInfo` describes packed layouts such as 5-6-5: a constructor taking the pixel size and channel widths, and the
  `BitsPerPixel` and `ChannelBits` properties. `BytesPerPixel` and `BitsPerChannel` are unchanged for every 1.0 type.
- `IPixelType` gains `FromRgba16` and `ToRgba16` with default bodies, so types written against 1.0 keep compiling.
  Conversions between integer types wider than 8 bits now go through `Rgba16` and are exact; results for the 1.0 types are
  unchanged.
- Writers and color operations handle unknown, packed and premultiplied layouts: PNG and PNM write 8-bit samples for packed
  types no wider than 8 bits, and an unknown layout without alpha is written as RGB. Color operations never read premultiplied
  bytes as straight values.

## 1.0.0 (unreleased)

The first public release.

### Formats

- PNG and APNG: every color type and bit depth, interlaced, transparency, text, EXIF, XMP, ICC; writing with adaptive filtering,
  built palettes and changed-rectangle animation.
- JPEG: baseline and progressive, any subsampling, gray, CMYK and YCCK, scaled decode, pixels identical to libjpeg-turbo;
  writing at quality 1 to 100 with optimized Huffman tables, EXIF, XMP and ICC.
- GIF: every disposal, interlaced, transparency, loop count; writing with exact or quantized palettes (Wu's quantizer,
  Floyd–Steinberg or ordered dithering) and frame optimization.
- BMP: every Windows and OS/2 2.x header, 1 to 32 bits, RLE4, RLE8, RLE24, bit fields, embedded PNG and JPEG; writing 24 and
  32-bit and 1, 2, 4 and 8-bit palettes.
- TGA: color-mapped, true-color and gray, raw and RLE, all origins, the TGA 2.0 footer and extension area.
- QOI: the whole format, with output byte-identical to the reference encoder.
- PBM, PGM, PPM and PAM: plain and binary, any maxval.

### Operations

Resize, fit and cover with six filters, in encoded or linear light; crop, pad, flip, rotate, auto-orient; brightness, contrast,
saturation, hue, sepia, grayscale, invert, opacity and color matrices; Gaussian and box blur, unsharp mask and any convolution;
source-over drawing of whole images or areas. Chains are planned before they run and run in parallel bands of rows.

### Core

- Nine pixel types, and outside pixel types through `IPixelType<TSelf>`.
- Editable frames for animations; per-frame delays and loop counts.
- Typed EXIF reading and editing; XMP, ICC, text and resolution kept through edits and saves.
- Safety limits checked before any allocation; lenient or strict recovery from damaged files.
- Pooled pixel memory with live usage counts and opt-in leak tracking.
- Custom operations and formats through public interfaces, including animated formats.

### Diagnostics

- `EgamiContext.Diagnostics` takes an `Action<EgamiDiagnostic>` that receives Egami's warnings and notable decisions, so one line
  sends them to `ILogger`, Serilog or the console; Egami takes no logging dependency. Off by default and free: with no handler
  nothing is built, and a load or save allocates exactly what it would without diagnostics.
- Each `EgamiDiagnostic` has a level (`Warning`, `Info`, `Debug`; `EgamiContext.DiagnosticLevel` sets the lowest delivered), a
  source, a stable id such as `Png.ChecksumMismatch`, a message, and the byte offset and frame when known. Every id is listed in
  the diagnostics article.
- `RasterMetadata.Warnings` is an `IReadOnlyList<EgamiDiagnostic>`: every problem a lenient read recovered from, with its id. One
  id is kept at most 10 times per call, then counted; the handler gets the same list as each call ends.
- Saves report what they lose without being asked (alpha, depth, CMYK inks, frames, metadata the format cannot hold), and Info
  and Debug events say what Egami chose: the format detected, a scaled decode, conversion on load, writer settings, parallel
  bands, chain planning.
- `EgamiContext.StartDiagnostics()` and `EgamiInput.Diagnostics` let format plug-ins and other libraries report through the same
  handler, filter and cap; `EgamiInput.Warn` takes an id.
