# Formats

Egami reads and writes PNG, APNG, JPEG, GIF, BMP, TGA, QOI and the Netpbm formats (PBM, PGM, PPM, PAM). Every reader checks sizes
against the safety limits before it allocates, and every reader has been fuzzed.

## At a glance

| Format | Reads | Writes | Natural pixel type |
| --- | --- | --- | --- |
| PNG | Every color type and bit depth, interlaced, transparency, text, EXIF, XMP, ICC | Every color type and bit depth, adaptive filtering, built palettes | The file's own: `Gray8`, `Gray16`, `GrayAlpha8`, `Rgb8`, `Rgba8` or `Rgba16` |
| APNG | Every PNG color type and depth, every disposal and blend | Any raster with more than one frame, changed rectangles only | `Rgba8`, or `Rgba16` for 16-bit |
| JPEG | Baseline and progressive, 8-bit, any subsampling, gray, CMYK, YCCK, scaled decode, EXIF, XMP, ICC | Baseline, quality 1 to 100, 4:4:4, 4:2:2, 4:2:0 or gray, optimized Huffman tables | `Gray8` or `Rgb8` |
| GIF | 87a and 89a, every disposal, interlaced, transparency, loop count, comments, XMP, ICC | 89a, exact or quantized palettes, global or per frame, dithering, frame optimization | `Rgba8` |
| BMP | Core, info, V2 to V5 and OS/2 2.x headers; 1 to 32 bits; RLE4, RLE8, RLE24; bit fields; embedded PNG and JPEG; V5 ICC | 24-bit, 32-bit with alpha, 1, 2, 4 or 8-bit palettes; resolution and ICC | `Bgr8`, or `Bgra8` with alpha |
| TGA | Color-mapped, true-color and gray, raw and RLE, 8 to 32 bits, all origins, TGA 2.0 footer and extension area | 8-bit gray, 24 and 32-bit, 8-bit palette, raw or RLE, TGA 2.0 footer | `Bgr8`, `Bgra8`, `Gray8` or `GrayAlpha8` |
| QOI | The whole format, 3 or 4 channels, both colorspaces | 3 or 4 channels, byte-identical to the reference encoder | `Rgb8` or `Rgba8` |
| PNM | P1 to P6 plain and binary, maxval 1 to 65,535, P7 PAM with alpha | P1 to P7, plain or binary | `Gray8`, `Gray16`, `GrayAlpha8`, `Rgb8`, `Rgba8` or `Rgba16` |

`Raster.Load` returns the natural type; `Raster.Load<TPixel>` decodes straight into the type you ask for, converting row by row.

[!code-csharp[](../../../tests/Egami.DocSamples/Formats.cs#natural-types)]

## Detection

Loading never trusts the file extension: the format comes from the first bytes, in the order PNG, JPEG, GIF, BMP, QOI, PNM,
then TGA. TGA has no signature, so it is matched only when its whole 18-byte header is consistent. No file of another format in
Egami's test corpora is taken for TGA, and neither is any of a million random headers. When you know the format, register only
that one:

[!code-csharp[](../../../tests/Egami.DocSamples/Formats.cs#one-format)]

Saving to a path picks the writer from the extension; an animated raster saved as `.png` writes an APNG. Saving to a stream
takes the format's `Info`.

## PNG

[!code-csharp[](../../../tests/Egami.DocSamples/Formats.cs#png)]

| Option | Default | Does |
| --- | --- | --- |
| `ColorType`, `BitDepth` | The pixel type's own | Force a layout, such as `PngColorType.Palette` |
| `Reduce` | `false` | Scan the image and write the smallest exact form |
| `Palette` | 256 colors, Floyd–Steinberg | How a palette is built when `ColorType` is `Palette` and the image has more colors |
| `CompressionLevel` | 5 | zlib level, 0 to 9 |
| `Filter` | `Adaptive` | Row filter, or one fixed filter |
| `FirstFrameOnly` | `false` | Write a still PNG from an animation |

Kept for round trips in `PngDetails`: gamma, chromaticities, sRGB intent, cICP, the ICC profile name, and every chunk Egami does
not read itself.

## BMP

[!code-csharp[](../../../tests/Egami.DocSamples/Formats.cs#bmp)]

A plain 32-bit BMP's fourth byte is used as alpha unless it is 0 in every pixel, in which case the image loads opaque; an alpha
mask in the header is always used. Pixels that an RLE delta skips are black. `BmpPixelFormat.Indexed2` writes Windows CE 2-bit
files, which many desktop readers cannot open, so `Auto` never picks it. Resolution is written in pixels per meter, and an ICC
profile switches the header to V5 with the profile embedded.

## TGA

[!code-csharp[](../../../tests/Egami.DocSamples/Formats.cs#tga)]

The descriptor's attribute bits say whether the extra bits are alpha, and a TGA 2.0 alpha type of "none" overrides them;
premultiplied alpha is divided out on load. The extension area's author and comments become `Author` and `Comment` text entries,
and the writer stores them back. `TgaPixelFormat.Bgr24` drops alpha rather than blending it onto a background.

## QOI

[!code-csharp[](../../../tests/Egami.DocSamples/Formats.cs#qoi)]

QOI's colorspace byte maps to `RasterMetadata.ColorSpace` (sRGB or linear) and back.

## PNM

[!code-csharp[](../../../tests/Egami.DocSamples/Formats.cs#pnm)]

Maxvals other than 255 and 65,535 are scaled exactly, as round(v × 255 / maxval). A PAM with an unknown tuple type is read by its
depth, with a warning. A stream with several images reads the first and warns. `#` comments become `Comment` text entries and
are written back.

## What each writer drops

Writers never change the raster. When a format cannot hold something, it is left out:

- **Alpha**: JPEG (unless `JpegWriterOptions.Background` blends it onto a color), BMP and TGA with `Bgr24`, PPM, QOI with
  `IncludeAlpha = false`.
- **Frames after the first**: JPEG, BMP, TGA, QOI and PNM.
- **Metadata**: every writer keeps what its format can carry; `StripMetadata = true` on any writer options drops it all.

See the [API reference](../../api/Egami.Formats.yml) for every option.
