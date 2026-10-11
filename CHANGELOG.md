# Changelog

All notable changes to Egami are listed here. Egami follows [Semantic Versioning](https://semver.org): from 1.0, public API
changes that break code come only in a new major version.

## Unreleased

Additive to the 1.0 API: nothing in it changes or goes away.

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

### Streaming decode (in progress)

- `RasterDecoder`: open an image and read it a band of rows (`ReadRows`) or a frame (`ReadFrame`) at a time, in any pixel
  type. Formats are decoded into a buffer first for now; `RasterInfo.Access` and `WorkingBytes` say so.
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
