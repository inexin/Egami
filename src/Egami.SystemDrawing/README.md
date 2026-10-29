# Egami.SystemDrawing

Egami.SystemDrawing is a System.Drawing drop-in: System.Drawing.Common's API on every OS, in managed code.

- `Bitmap`, `Image` and `Icon` with all 14 GDI+ pixel formats, `GetPixel`, `SetPixel` and `LockBits`, converted exactly as GDI+
  converts them.
- `Image.FromFile`, `FromStream` and `Save` through Egami's codecs, with GDI+'s pixel formats, palettes, frames, property items,
  `ImageCodecInfo` and encoder parameters.
- `Graphics` with every Draw, Fill and DrawImage overload, transforms, clipping and regions, pens, brushes and `GraphicsPath`,
  measured against GDI+ on Windows.
- Fonts and text: `Font`, `FontFamily`, `StringFormat`, `DrawString` and `MeasureString` laid out as GDI+ lays text out, with
  ClearType; metric-compatible stand-ins for Arial, Times New Roman and Courier New where Windows' fonts are missing.
- Metafiles (WMF, EMF, EMF+), the GDI+ 1.1 effects, `BufferedGraphics` and `ImageAnimator`.
- Swap the package reference: `System.Drawing` and `Egami.SystemDrawing` come in as implicit usings. Members that need Windows
  handles throw `PlatformNotSupportedException` and warn at compile time.

This is a preview (1.0.0-preview.1). See the
[System.Drawing drop-in guide](https://inexin.github.io/Egami/v1/articles/guides/system-drawing.html) for moving code over and
the known differences.

Egami is MIT-licensed, fully managed C# for .NET 10: no native binaries, no P/Invoke and no third-party dependencies, trimming
and Native AOT clean. Documentation: [inexin.github.io/Egami](https://inexin.github.io/Egami/). Source and changelog:
[github.com/inexin/Egami](https://github.com/inexin/Egami).
