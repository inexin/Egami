# Changelog

All notable changes to Egami are listed here. Egami follows [Semantic Versioning](https://semver.org): from 1.0, public API
changes that break code come only in a new major version.

## Unreleased

Additive to the 1.0 API: nothing in it changes or goes away.

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
- A 32-bit BMP whose alpha byte is zero everywhere now loads into 16-bit and premultiplied pixel types without a lossy round trip
  when the stream can seek.
- Behaviour change: `ReaderOptions.MinimumSize` now reduces every format, by the largest whole factor that keeps the image at
  least that big, averaging blocks of pixels. JPEG keeps its 1/2, 1/4 and 1/8 IDCT scaling and averages blocks beyond 1/8.

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
