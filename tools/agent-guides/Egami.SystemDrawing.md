# Egami.SystemDrawing: guide for coding agents

Egami.SystemDrawing is a System.Drawing drop-in: System.Drawing.Common's API (`Bitmap`, `Image`, `Graphics`, `Pen`, `Brush`,
`Font`, `GraphicsPath`, `ImageCodecInfo` and the rest) on every OS, in managed code, matched against GDI+ on Windows. It is a
preview.

Namespaces: `Egami.SystemDrawing` and its `Drawing2D`, `Imaging` and `Text` sub-namespaces hold the classes;
`System.Drawing` still holds the BCL structs (`Color`, `Point`, `Rectangle`, `Size`). Both come in as implicit usings when the
package is referenced, so most System.Drawing code compiles after swapping the package reference.

## Entry points

- The System.Drawing API as you know it: `new Bitmap(...)`, `Image.FromFile`, `Graphics.FromImage`, `DrawString`, `Save`.
- `SystemDrawingOptions`: process-wide settings such as `FontSubstitutes` and `ArgbDrawing`.
- Ways out to Egami's own API, one call at a time: `bitmap.AsRaster()` (the bitmap's pixels as a raster), `Bitmap.FromRaster`,
  `graphics.Canvas` (an Egami.Drawing canvas) and `font.ToEgamiFont()`.

## Samples

A bitmap, as with System.Drawing.Common:

{{sample SystemDrawingPackage.cs#bitmap}}

Graphics:

{{sample SystemDrawingPackage.cs#graphics}}

Fonts, and stand-ins for missing ones:

{{sample SystemDrawingPackage.cs#fonts}}

Moving on to Egami's own API:

{{sample SystemDrawingPackage.cs#escape-hatches}}

## Common mistakes

- Keeping a reference to System.Drawing.Common next to this package. Swap it: both define the same class names.
- Calling members that need Windows handles (`GetHbitmap`, `GetHdc` and similar). They throw
  `PlatformNotSupportedException` and warn at compile time; keep that code Windows-only.
- Text on Linux in fonts that are not installed. Without help, missing families fall back to a generic sans-serif; reference
  Egami.Fonts.Substitutes and set `SystemDrawingOptions.FontSubstitutes = SubstituteFonts.Find` to keep line breaks.
- Holding an `AsRaster()` view across `RotateFlip`, a pixel format change or `SelectActiveFrame`. Those give the bitmap new
  pixels; fetch the view again.
- Writing new code against System.Drawing. Use it to keep old code running; new code reads better on Egami's own API (see the
  migration guide).

## More

- System.Drawing drop-in: https://inexin.github.io/Egami/v1/articles/guides/system-drawing.html
- Text as GDI+ lays it out: https://inexin.github.io/Egami/v1/articles/guides/system-drawing-text.html
- Moving on to Egami's API: https://inexin.github.io/Egami/v1/articles/guides/system-drawing-migration.html
- Every page in one list: https://inexin.github.io/Egami/v1/llms.txt
- API reference: https://inexin.github.io/Egami/v1/api/Egami.SystemDrawing.html
