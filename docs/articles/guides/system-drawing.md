# System.Drawing drop-in

Egami.SystemDrawing gives code written for System.Drawing.Common its API on every OS, in managed code: `Bitmap`, `Image`,
`Icon`, pixel formats, `LockBits`, the codecs and their encoder parameters, frames and property items. System.Drawing.Common
runs only on Windows since .NET 6; with this package the same code runs on Linux, macOS and in containers, drawn by Egami.

The package is a preview (1.0.0-preview.1). This release has bitmaps, pixel access, codecs and icons. `Graphics`, pens,
brushes, paths, regions, fonts and text follow in the next previews.

## Moving code over

Replace the System.Drawing.Common package reference with Egami.SystemDrawing. With implicit usings on, the package adds
`System.Drawing` and `Egami.SystemDrawing` as implicit usings, so most files compile unchanged; files that name
`System.Drawing.Imaging` types change that line to `using Egami.SystemDrawing.Imaging;`. Set `EgamiSystemDrawingImplicitUsings`
to `false` in the project file to leave the implicit usings out.

`Color`, `Point`, `Size`, `Rectangle`, their float forms, `KnownColor`, `SystemColors` and `ColorTranslator` are .NET's own
System.Drawing.Primitives types, which every OS has, so values pass between your code, other libraries and Egami unchanged.
Everything else (`Bitmap`, `Image`, `PixelFormat`, `ImageFormat` and the rest) is defined by the package with
System.Drawing's names, members, overloads, defaults and exceptions: `ArgumentException` for "Parameter is not valid",
`ExternalException` for GDI+'s generic error, `OutOfMemoryException` from `Image.FromFile` for a file it cannot read.

The package is source-compatible, not binary-compatible: your code recompiles against it, while a DLL built against
System.Drawing.Common keeps using System.Drawing.Common.

[!code-csharp[](../../../tests/Egami.DocSamples/SystemDrawingPackage.cs#bitmap)]

`GraphicsUnit` and `RotateFlipType` (with System.Drawing's shared values, such as `RotateNoneFlipY` equal to `Rotate180FlipX`)
live in `Egami.SystemDrawing`, as they do in `System.Drawing`.

## Bitmaps and pixel formats

A `Bitmap` holds its pixels in GDI+'s own memory layout for each `PixelFormat`: rows of the format's bits, each padded to a
multiple of four bytes. The 14 bitmap formats are all there, and pixels convert exactly as GDI+ converts them, measured on
Windows for every 8-bit value and every color and alpha pair:

- 5 and 6-bit channels (`Format16bppRgb555`, `Format16bppRgb565`, `Format16bppArgb1555`) truncate when written and repeat their
  high bits when read; 1555's alpha bit is set from alpha 128.
- `Format32bppPArgb` premultiplies with rounding and divides out through GDI+'s 16-bit reciprocal of alpha.
- `Format32bppRgb` keeps the fourth byte it is given and reads as opaque.
- `Format48bppRgb`, `Format64bppArgb` and `Format64bppPArgb` hold linear light from 0 to 8192 through the sRGB curve, as GDI+
  does, so 8-bit colors round-trip.
- Indexed formats look up their palette; `SetPixel` on them throws `InvalidOperationException`, and `Format16bppGrayScale`
  bitmaps exist but `GetPixel` and `SetPixel` throw, as in GDI+.

`Image.GetPixelFormatSize`, `IsAlphaPixelFormat`, `IsExtendedPixelFormat` and `IsCanonicalPixelFormat` read the format's bits,
and `Image.Flags` holds `ImageFlags` values: alpha, color space, and for a file, read-only and a stated resolution.

[!code-csharp[](../../../tests/Egami.DocSamples/SystemDrawingPackage.cs#pixel-formats)]

`Clone(rect, format)` copies an area into another format, `ConvertFormat` converts in place, `MakeTransparent` keys out a color
(the bottom-left pixel's by default) into 32-bit ARGB, `RotateFlip` turns and mirrors, and `GetThumbnailImage` makes a 32-bit
premultiplied copy. `new Bitmap(image, size)` draws another image at a size, bilinearly, as GDI+'s DrawImage does. A bitmap made
with `new Bitmap(width, height, stride, format, scan0)` reads and writes the caller's memory in place.

## Locking pixels

`LockBits` with an `ImageLockMode` returns a `BitmapData`: where the first locked row is, the bytes from one row to the next, and
the size and format. Locked in the bitmap's own format, the pointer is into the bitmap's memory. Locked in another format, it is
a converted copy, written back on `UnlockBits` when the lock writes. `ImageLockMode.UserInputBuffer` locks into a buffer the
caller's `BitmapData` points to. While a bitmap is locked, `GetPixel`, `SetPixel` and a second lock throw `ExternalException`, as
in GDI+.

[!code-csharp[](../../../tests/Egami.DocSamples/SystemDrawingPackage.cs#lockbits)]

## Palettes

Indexed bitmaps start with GDI+'s default palettes: black and white, the 16 VGA colors, or for 8 bits the VGA colors, 24
transparent entries and a 6 by 6 by 6 cube. `Image.Palette` hands out a copy as a `ColorPalette`; set it back to change it. Its
`Flags` hold `PaletteFlags`. `new ColorPalette(PaletteType)` builds GDI+'s fixed halftone palettes, and
`ColorPalette.CreateOptimalPalette` builds one from a bitmap's colors with Egami's palette builder.

Converting to an indexed format maps each color as GDI+ does, through a table of 16 levels a channel: `Clone` uses GDI+'s
halftone palettes (2, 16 or 224 colors), a lock uses the format's default palette, and `ConvertFormat` the palette or
`PaletteType` it is given. Every `DitherType` maps to the nearest color; Egami does not dither yet.

[!code-csharp[](../../../tests/Egami.DocSamples/SystemDrawingPackage.cs#palettes)]

## Loading and saving

`Image.FromFile`, `Image.FromStream` and the `Bitmap` constructors read through Egami's codecs, which read every format Egami
does, more than GDI+. Images come in the pixel format GDI+ gives each file type: 24-bit PNG and JPEG as `Format24bppRgb`,
palette PNG, GIF and BMP files as their indexed formats with their palettes, gray PNG and 16-bit gray TIFF as `Format32bppArgb`,
gray JPEG and 8-bit gray TIFF as `Format8bppIndexed` with a gray palette, plain 32-bit BMP as `Format32bppRgb`, 16-bit RGB TIFF
as `Format48bppRgb`, icons and animated GIFs as `Format32bppArgb`. The resolution is the file's, computed as GDI+ computes it
(3780 pixels a meter is 96.01199), or 96. `RawFormat` is the file's `ImageFormat`.

`Save` writes BMP, PNG, GIF, TIFF and JPEG, the encoders `ImageCodecInfo.GetImageEncoders` lists with GDI+'s class ids, MIME
types and `ImageCodecFlags`. An `EncoderParameters` holds `EncoderParameter` settings, by `Encoder`: `Encoder.Quality` sets
JPEG quality (75 by default), `Encoder.Compression` with an `EncoderValue` sets TIFF compression (LZW by default),
`Encoder.ColorDepth` writes 1, 4 or 8-bit TIFF, and `Encoder.SaveFlag` with `SaveAdd` writes a multipage TIFF.
`EncoderParameterValueType` says what type a setting's values have. `GetEncoderParameterList` lists what each encoder takes,
as GDI+ lists it.

[!code-csharp[](../../../tests/Egami.DocSamples/SystemDrawingPackage.cs#codecs)]

BMP is written as GDI+ writes it, with the bitmap's own rows, so a 32-bit bitmap reads back as `Format32bppRgb`. GIF is written
with the default 8-bit palette.

## Frames and properties

A multipage TIFF counts its pages in `FrameDimension.Page` and an animated GIF its frames in `FrameDimension.Time`;
`GetFrameCount` and `SelectActiveFrame` choose one. `PropertyItems`, `GetPropertyItem`, `SetPropertyItem` and
`RemovePropertyItem` hold what GDI+ reports as `PropertyItem` values: the EXIF tags, every tag of a TIFF's first directory, a
JPEG's quantization tables (0x5090 and 0x5091), a PNG's gamma and pixel size, and a GIF's frame delays, loop count, global
palette, background and transparent index and comment, each with its tag id, type and little-endian bytes.

[!code-csharp[](../../../tests/Egami.DocSamples/SystemDrawingPackage.cs#frames)]

`GetThumbnailImage` takes an `Image.GetThumbnailImageAbort` callback, which, as in GDI+, is never called.

## Icons

`Icon` loads an ICO file and shows the image nearest the size asked for, 32 by 32 by default, chosen as System.Drawing chooses:
the smallest difference in width plus height, then the most bits a pixel. `ToBitmap` gives it as 32-bit ARGB, and `Save` writes
the file it came from. `Image.FromFile` on an icon takes the last image of the file, as GDI+'s decoder does.

[!code-csharp[](../../../tests/Egami.DocSamples/SystemDrawingPackage.cs#icon)]

## What throws on every OS

Members that need Windows handles exist so code compiles, throw `PlatformNotSupportedException` with a message naming what to
use instead, and carry `[Obsolete]` with one diagnostic id per group, so the compiler warns at each use. Suppress an id in the
project file (`<NoWarn>$(NoWarn);EGSD0001</NoWarn>`), around a call (`#pragma warning disable EGSD0001`), or raise it to an
error in `.editorconfig`.

<a id="egsd0001"></a>

- **EGSD0001, Windows handles**: `Bitmap.GetHbitmap`, `GetHicon`, `FromHicon` and `FromResource`, `Image.FromHbitmap`,
  `Icon.Handle` and `Icon.FromHandle`. Use `LockBits` for the pixels and `new Icon(path)` for icons.

<a id="egsd0002"></a>

- **EGSD0002, screen capture**: `Graphics.CopyFromScreen`, which comes with `Graphics`.

<a id="egsd0003"></a>

- **EGSD0003, Windows' own icons**: `Icon.ExtractAssociatedIcon` and `Icon.ExtractIcon`. Load an icon file instead.

## Known differences

The package is measured against System.Drawing on Windows with the same probe programs compiled against both (egami-compare,
`sysdrawing/`). Pixel formats, conversions, palettes, strides, locks and the codec list match GDI+ exactly. Where it differs:

- JPEG and GIF files Egami writes differ from GDI+'s in their pixels: the JPEG encoder and GIF color mapping are Egami's.
- `CreateOptimalPalette` chooses different colors.
- CMYK JPEG and TIFF files load as `Format24bppRgb`; GDI+ gives them a CMYK format System.Drawing has no name for.
- Egami reads BMP files GDI+ rejects (OS/2, 2-bit, RLE24, embedded PNG and JPEG).
