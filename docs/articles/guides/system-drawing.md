# System.Drawing drop-in

Egami.SystemDrawing gives code written for System.Drawing.Common its API on every OS, in managed code: `Bitmap`, `Image`,
`Icon`, pixel formats, `LockBits`, the codecs and their encoder parameters, frames and property items. System.Drawing.Common
runs only on Windows since .NET 6; with this package the same code runs on Linux, macOS and in containers, drawn by Egami.

The package is a preview (1.0.0-preview.1). This release has bitmaps, pixel access, codecs, icons, and `Graphics` with pens,
brushes, paths, transforms, clipping, regions and images. Fonts and text follow in the next previews.

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

## Drawing with Graphics

`Graphics.FromImage` draws on a bitmap of any pixel format GDI+ draws on; indexed formats throw `ArgumentException`, and 16-bit
gray and 16-bit ARGB 1555 throw `ExternalException`, as in GDI+. Each call draws at once, so `Flush` does nothing, and the bitmap
keeps what was drawn after `Dispose`; `FlushIntention` is accepted and changes nothing. As in GDI+, drawing goes through premultiplied ARGB with GDI+'s rounding, so a
translucent copy onto a format without alpha keeps the premultiplied color.

GDI+'s defaults are kept: shapes are aliased (`SmoothingMode.None`), pixel centers lie on whole coordinates
(`PixelOffsetMode.Default`; `Half` and `HighQuality` move them half a pixel, to Egami's own centers), images are sampled
bilinearly, and drawing blends (`CompositingMode.SourceOver`; `SourceCopy` replaces). `CompositingQuality` and `QualityMode`
values are kept but blend the same way. Settings read back as GDI+ stores them:
`SmoothingMode.HighQuality` reads as `AntiAlias`, `HighSpeed` and `Default` as `None`, `InterpolationMode.Low` as `Bilinear` and
`High` as `HighQualityBicubic`. `PageUnit` and `PageScale` scale page coordinates at the bitmap's resolution; with
`GraphicsUnit.Display`, the default, GDI+ ignores the page scale and so does the package.

[!code-csharp[](../../../tests/Egami.DocSamples/SystemDrawingPackage.cs#graphics)]

### Transforms and states

`Matrix` is a 3 by 2 affine transform with System.Drawing's members; `MatrixOrder.Prepend`, the default, applies a new
transform before the existing one. The world transform takes the same calls on `Graphics`, and `TransformPoints` maps points
between `CoordinateSpace.World`, `Page` and `Device`. `Save` and `Restore` keep the transform, page settings, clip and quality
settings; restoring a state drops every state saved after it. `BeginContainer` returns a `GraphicsContainer` and starts again from the defaults inside the
current transform and clip; with a destination and a source rectangle, the source is in the given unit and the container's own
coordinates are pixels, as in GDI+.

[!code-csharp[](../../../tests/Egami.DocSamples/SystemDrawingPackage.cs#transforms)]

### Clipping and regions

The clip is kept in device pixels, as GDI+ keeps it, so a later transform does not move it, and it holds the pixels whose
whole coordinates lie inside: closed at the top and left, open at the bottom and right, half a pixel in when the pixel offset is
set. `SetClip` takes a `CombineMode`. `ClipBounds` and `VisibleClipBounds` are its pixels' bounds in world coordinates. A `Region` keeps its shape as given;
`GetRegionScans`, `IsEmpty`, `Equals` and the bounds of a combined region are its pixels, rasterized the same way. `GetRegionData`
writes GDI+'s region format as a `RegionData`, with the checksum left as zero.

[!code-csharp[](../../../tests/Egami.DocSamples/SystemDrawingPackage.cs#clipping)]

### Shapes

`DrawLine`, `DrawRectangle`, `DrawEllipse`, `DrawArc`, `DrawPie`, `DrawPolygon`, `DrawBezier`, `DrawCurve`, `DrawClosedCurve`,
`DrawRoundedRectangle` and their `Fill` forms take every System.Drawing overload, including the span ones. Arcs are measured on
the ellipse as GDI+ measures them, cardinal splines use GDI+'s tension (0.5 by default), and a pen is never thinner than a
pixel. Polygons and closed curves fill even-odd unless given `FillMode.Winding`. `DrawPath` and `FillPath` draw a
`GraphicsPath`.

[!code-csharp[](../../../tests/Egami.DocSamples/SystemDrawingPackage.cs#shapes)]

### Pens

A `Pen` has GDI+'s settings and checks: `StartCap` and `EndCap` take every `LineCap`, the anchors centered on the end;
`LineJoin` with `MiterLimit` (at least 1); a `DashStyle` whose `DashPattern` is in multiples of the width, with a `DashCap` and
`DashOffset` (a solid pen's `DashPattern` throws `InvalidOperationException`, and `Custom` keeps the pattern of the style
before it); `CompoundArray` strips; `PenAlignment.Inset`, the only alignment GDI+ draws differently from centered; and a
`Transform` that shapes the nib, so a pen scaled 3 by 1 draws three times as wide across vertical lines. `CustomEndCap` and
`CustomStartCap` take an `AdjustableArrowCap` or a `CustomLineCap` with a stroke path; as in GDI+, a `CustomLineCap` with a fill
path throws `NotImplementedException`. `PenType` says what kind of brush the pen paints with. `Pens` and `SystemPens` hold
one-unit pens of the named and system colors, which cannot be changed or disposed.

[!code-csharp[](../../../tests/Egami.DocSamples/SystemDrawingPackage.cs#pens)]

### Brushes

`HatchBrush` draws the 53 `HatchStyle` patterns pixel for pixel as GDI+ draws them on a bitmap, from the device origin moved by
`RenderingOrigin`, including GDI+'s smoothed forward, backward and crossed diagonals. `TextureBrush` tiles an image (or part of
it) through its transform, by its `WrapMode`; given `ImageAttributes`, it takes their wrap mode, `Clamp` unless they set one.
`LinearGradientBrush` runs across a rectangle at an angle, a `LinearGradientMode` or between two points, with GDI+'s brush
transform: the far corners lie on the end colors, and the diagonal modes and a scaleable angle work in the rectangle stretched to
a square. Its `Blend` falls off from `SetSigmaBellShape` (511 samples of the normal curve, as GDI+ takes them) or
`SetBlendTriangularShape`, or a `ColorBlend` sets preset colors, after which `Blend` is null. `PathGradientBrush` runs from a
center color at the mean of the boundary's points out to the surrounding colors. `Brushes` and `SystemBrushes` hold solid
brushes of the named and system colors; as in System.Drawing, the color of one of the `Brushes` can be changed. Every brush
derives from `Brush`.

[!code-csharp[](../../../tests/Egami.DocSamples/SystemDrawingPackage.cs#brushes)]

### Paths

A `GraphicsPath` keeps GDI+'s points and `PathPointType` types, built as GDI+ builds them: lines, arcs, Beziers and curves join
the open figure (a repeated point is dropped), while rectangles, ellipses, pies, polygons and closed curves are figures of their
own. Arcs are cubic Beziers of 90 degrees of the ellipse's own angle from the start, the remainder last. `PathPoints`,
`PathTypes` and `PathData` hand out copies. `Flatten`, `Widen` and `Warp` (a `WarpMode` of perspective or bilinear, from four
points or three) replace the figures; `Reverse` keeps each segment's kind, the closes and the markers. `GetBounds` with a matrix
transforms the bounds' corners and grows them for a pen as GDI+ does, and `IsVisible` and `IsOutlineVisible` test the pixel a
point rounds to. `GraphicsPathIterator` walks figures, runs of one type and markers. A path also makes a `Region`, a clip, and a
path gradient's boundary.

[!code-csharp[](../../../tests/Egami.DocSamples/SystemDrawingPackage.cs#paths)]

### Images

`DrawImage(image, x, y)` and `DrawImageUnscaled` draw an image at its physical size: its pixels at its resolution, in page
units at the bitmap's resolution. The other overloads stretch an image or part of it into a rectangle or a parallelogram, with
the source in any `GraphicsUnit`. As in GDI+, the edges fade where sampling reaches past the image, unless `ImageAttributes`
sets a wrap mode; `ImageAttributes` also applies color matrices (with a `ColorMatrixFlag` for grays), gamma, thresholds, color
keys, `ColorMap` remap tables (by `ColorMapType`) and `ColorChannelFlag` output channels, each for every `ColorAdjustType` or
one of them. A `DrawImageAbort` callback that returns true stops the draw with `ExternalException`. `DrawIcon` stretches an icon,
and `DrawIconUnstretched` draws it at its size.

[!code-csharp[](../../../tests/Egami.DocSamples/SystemDrawingPackage.cs#images)]

## What throws on every OS

Members that need Windows handles exist so code compiles, throw `PlatformNotSupportedException` with a message naming what to
use instead, and carry `[Obsolete]` with one diagnostic id per group, so the compiler warns at each use. Suppress an id in the
project file (`<NoWarn>$(NoWarn);EGSD0001</NoWarn>`), around a call (`#pragma warning disable EGSD0001`), or raise it to an
error in `.editorconfig`.

<a id="egsd0001"></a>

- **EGSD0001, Windows handles**: `Bitmap.GetHbitmap`, `GetHicon`, `FromHicon` and `FromResource`, `Image.FromHbitmap`,
  `Icon.Handle` and `Icon.FromHandle`, `Graphics.FromHdc`, `FromHwnd`, `GetHdc`, `ReleaseHdc` and `GetHalftonePalette`, and
  `Region.FromHrgn` and `GetHrgn`. Use `LockBits` for the pixels, `new Icon(path)` for icons and `Graphics.FromImage` to draw.

<a id="egsd0002"></a>

- **EGSD0002, screen capture**: `Graphics.CopyFromScreen`, with or without a `CopyPixelOperation`.

<a id="egsd0003"></a>

- **EGSD0003, Windows' own icons**: `Icon.ExtractAssociatedIcon` and `Icon.ExtractIcon`. Load an icon file instead.

## Known differences

The package is measured against System.Drawing on Windows with the same probe programs compiled against both (egami-compare,
`sysdrawing/`). Pixel formats, conversions, palettes, strides, locks and the codec list match GDI+ exactly. Where it differs:

- JPEG and GIF files Egami writes differ from GDI+'s in their pixels: the JPEG encoder and GIF color mapping are Egami's.
- `CreateOptimalPalette` chooses different colors.
- CMYK JPEG and TIFF files load as `Format24bppRgb`; GDI+ gives them a CMYK format System.Drawing has no name for.
- Egami reads BMP files GDI+ rejects (OS/2, 2-bit, RLE24, embedded PNG and JPEG).
- Antialiased edges are Egami's: about 96 percent of the pixels of an antialiased shape match GDI+ within 8 levels, and aliased
  shapes match on about 99.8 percent of their pixels.
- `HighQualityBilinear` and `HighQualityBicubic` sample as plain bilinear and bicubic, without GDI+'s wider filters, and an image
  drawn much smaller is not prefiltered.
- Drawing onto 16-bit bitmaps does not dither, and `SourceCopy` with antialiasing blends its edges with what is below instead of
  making them transparent.
- Compound pens draw their strips on both sides of the line, and `PathGradientBrush.FocusScales` is kept but not drawn.
- `Flatten` takes as many steps as GDI+ but evaluates the curve exactly, so its points differ from GDI+'s, which are rounded to
  sixteenths of a pixel. Rounded rectangles have no zero-length segments, which GDI+'s float arithmetic adds.
