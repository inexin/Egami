# Egami.SystemDrawing

Egami.SystemDrawing is a System.Drawing drop-in: System.Drawing.Common's API on every OS, in managed code.

- `Bitmap`, `Image` and `Icon` with all 14 GDI+ pixel formats, `GetPixel`, `SetPixel` and `LockBits`, converted exactly as GDI+
  converts them.
- `Image.FromFile`, `FromStream` and `Save` through Egami's codecs, with GDI+'s pixel formats, palettes, frames, property items,
  `ImageCodecInfo` and encoder parameters.
- Swap the package reference: `System.Drawing` and `Egami.SystemDrawing` come in as implicit usings.

This is a preview: `Graphics`, pens, brushes, paths, fonts and text follow in the next previews. See the
[System.Drawing drop-in guide](https://inexin.github.io/Egami/v1/articles/guides/system-drawing.html).

Egami is MIT-licensed, fully managed C# for .NET 10: no native binaries, no P/Invoke and no third-party dependencies, trimming
and Native AOT clean. Documentation: [inexin.github.io/Egami](https://inexin.github.io/Egami/). Source and changelog:
[github.com/inexin/Egami](https://github.com/inexin/Egami).
