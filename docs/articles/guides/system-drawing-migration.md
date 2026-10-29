# Migrating from System.Drawing to Egami

Egami.SystemDrawing runs System.Drawing code unchanged; this guide moves that code to Egami's own API one piece at a time, with
the four escape hatches that join the two: `Bitmap.AsRaster`, `Bitmap.FromRaster`, `Graphics.Canvas` and `Font.ToEgamiFont`. At
every step the program builds, runs and gives the same pictures, so the move can stop anywhere.

## Step 0: the drop-in

Replace the System.Drawing.Common package reference with Egami.SystemDrawing, as the [drop-in guide](system-drawing.md#moving-code-over)
describes: `System.Drawing` and `Egami.SystemDrawing` come in as implicit usings, and files that name `System.Drawing.Imaging`,
`System.Drawing.Drawing2D` or `System.Drawing.Text` change those lines to `Egami.SystemDrawing.Imaging`, `.Drawing2D` and
`.Text`. Members that need Windows handles compile with a warning, EGSD0001 to EGSD0004, and throw when called: each warning
marks code that has to change anyway (an HBITMAP becomes `LockBits` or `AsRaster`, `CopyFromScreen` has no replacement, an
`Icon` comes from a file). The [drop-in guide's list](system-drawing.md#what-throws-on-every-os) names every member.

## The strategy

Keep `Bitmap`, `Image` and `Graphics` at the edges of the program, where they meet other code and public APIs, and move the hot
spots first: pixel loops, resizing, filters, batch encoding. An escape hatch turns a bitmap into an Egami raster for one part of a
routine and back for the rest, so each change is small and tested on its own. When no `Bitmap` crosses a boundary any more, drop
the package.

## Pixels: Bitmap.AsRaster

`AsRaster` gives a bitmap's pixels as a `Raster<TPixel>` without copying them for 32-bit ARGB (`Raster<Bgra8>`), premultiplied
ARGB (`Raster<PremulBgra8>`), 24-bit RGB (`Raster<Bgr8>`) and the 16-bit formats; other formats give an 8-bit RGBA copy. Egami's
operations (`Apply` changes a raster, `Derive` makes a new one) and its row spans then work on the bitmap itself, in place of
`LockBits` loops, `ImageAttributes` color matrices and hand-written filters.

The view stays valid as long as you hold it, but RotateFlip, MakeTransparent, a format change (`ConvertFormat`) and
SelectActiveFrame give the bitmap new pixels, which an old view no longer shows: fetch the view again after them.

[!code-csharp[](../../../tests/Egami.DocSamples/SystemDrawingMigration.cs#as-raster)]

## Results back: Bitmap.FromRaster

`Bitmap.FromRaster` copies any Egami raster into a bitmap of the matching format (`Bgra8` into 32-bit ARGB, `Bgr8` into 24-bit
RGB, other pixel types through 8-bit RGBA), for code that still takes a `Bitmap` or `Image`.

[!code-csharp[](../../../tests/Egami.DocSamples/SystemDrawingMigration.cs#from-raster)]

## Drawing: Graphics.Canvas

`Graphics.Canvas` is an Egami.Drawing canvas over a 32-bit bitmap, set to the graphics' world transform and clip, so Egami
paths, paints, gradients, images and text can be drawn between `Graphics` calls. A routine moves call by call; once it only uses
the canvas, draw on `raster.CreateCanvas()` instead.

[!code-csharp[](../../../tests/Egami.DocSamples/SystemDrawingMigration.cs#canvas)]

## Text: Font.ToEgamiFont

`Font.ToEgamiFont` gives the same family, style and size as an Egami.Fonts font, sized in pixels at 96 dpi by default, with
synthetic bold or italic where the family lacks the face. Egami shapes text (kerning, ligatures, Arabic, Indic and the other
complex scripts), lays out paragraphs with `TextLayout` and draws with `Canvas.DrawText`; `Font.MeasureText` replaces
`MeasureString`.

[!code-csharp[](../../../tests/Egami.DocSamples/SystemDrawingMigration.cs#egami-font)]

## Files: Raster.Load and Save

`Image.FromFile` and `Image.FromStream` become `Raster.Load`, which reads every format Egami knows; `Raster.Probe` reads the
size, format and frames from the header alone. `Save` takes the format's own writer options (`JpegWriterOptions`,
`PngWriterOptions`, `WebpWriterOptions` and the others) instead of `ImageCodecInfo` and `EncoderParameters`, and metadata stays
in the raster's `Metadata` instead of `PropertyItem`s. The [formats overview](../formats/index.md) and each format's page cover the options.

[!code-csharp[](../../../tests/Egami.DocSamples/SystemDrawingMigration.cs#load-save)]

## What maps to what

| System.Drawing | Egami |
| --- | --- |
| `Bitmap`, `Image` | `Raster<TPixel>`, such as `Raster<Rgba8>`; `Raster` when the pixel type does not matter |
| `PixelFormat` | the pixel type: `Rgba8`, `Bgra8`, `PremulBgra8`, `Rgb8`, `Gray8`, `Rgba16` and the others |
| `GetPixel`, `SetPixel`, `LockBits` | `Raster<TPixel>.GetPixel`, `SetPixel` and `Row(y)` spans |
| `Image.FromFile`, `FromStream`, `Save`, `EncoderParameters` | `Raster.Load`, `Raster.Probe`, `Save` with the format's writer options |
| `PropertyItem`, `GetPropertyItem` | `Raster.Metadata` (EXIF, XMP, IPTC, ICC) |
| `RotateFlip`, `GetThumbnailImage`, `Clone(rect)` | operations: `Rotate`, `FlipHorizontal`, `FlipVertical`, `Fit`, `Resize`, `Crop` |
| `ImageAttributes`, `ColorMatrix`, `SetGamma` | operations: `ColorMatrix`, `Gamma`, `Grayscale`, `Brightness` and the rest |
| `Graphics`, `Graphics.FromImage` | `Canvas`, from `raster.CreateCanvas()` |
| `Pen`, `DashStyle`, `LineCap`, `LineJoin` | `Stroke` (width, dashes, caps, joins) with a `Paint` |
| `SolidBrush`, `LinearGradientBrush`, `PathGradientBrush`, `TextureBrush` | `SolidPaint`, `LinearGradient`, `RadialGradient`, `ImagePaint` |
| `GraphicsPath`, `Region` | `Path`, `PathBuilder`, `Path.Union` and the other path operations |
| `Matrix`, `TranslateTransform`, `RotateTransform` | `System.Numerics.Matrix3x2`, `Canvas.Concat`, `Translate`, `Rotate`, `Scale` |
| `SetClip`, `IntersectClip` | `Canvas.ClipRect`, `ClipPath` |
| `Font`, `FontFamily`, `InstalledFontCollection` | `Egami.Fonts.Font`, `Typeface`, `FontCollection.System` |
| `DrawString`, `MeasureString`, `StringFormat` | `Canvas.DrawText`, `Font.MeasureText`, `TextLayout` with a `ParagraphStyle` |
| `Metafile` | `Egami.Metafiles.Metafile` |
| `Icon` | ICO and CUR files through `Raster.Load` and `Save` |

## The last step

When no `Bitmap`, `Image`, `Graphics` or `Font` crosses a boundary of your code any more, remove the Egami.SystemDrawing
package reference and its implicit usings. What is left uses Egami, Egami.Drawing and Egami.Fonts directly; the compiler lists
anything that still names a drop-in type.

## Recipes

### Move a thumbnail with a watermark in three steps

A routine that scales a photo to 200 pixels wide, writes a caption on it and saves a JPEG, as System.Drawing code writes it. It
runs unchanged on the drop-in:

[!code-csharp[](../../../tests/Egami.DocSamples/SystemDrawingMigration.cs#step1)]

The scaling moves to Egami first: the photo loads as a raster, `Resize` makes the thumbnail, and `Bitmap.FromRaster` hands it
to the drawing and saving code, which does not change:

[!code-csharp[](../../../tests/Egami.DocSamples/SystemDrawingMigration.cs#step2)]

Then the caption and the save move too, and the routine uses Egami alone:

[!code-csharp[](../../../tests/Egami.DocSamples/SystemDrawingMigration.cs#step3)]
