# Changelog

All notable changes to Egami are listed here. Egami follows [Semantic Versioning](https://semver.org): from 1.0, public API
changes that break code come only in a new major version.

## Unreleased

Additive to the 1.0 API: nothing in it changes or goes away.

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
