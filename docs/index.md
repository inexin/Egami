# Egami

Egami ("image" backwards) is an open source 2D imaging library for .NET 10 that loads, edits, draws and saves images.

- **MIT-licensed**, with no strings for commercial use.
- **Fully managed C#**: no native binaries, no P/Invoke, and no third-party dependencies.
- **Safe on untrusted files**: sizes are checked before anything is allocated, and every reader is fuzz-tested.
- **Trimming and Native AOT clean**, on every OS and CPU .NET 10 supports.
- **Formats**: PNG and APNG, JPEG, GIF, WebP, BMP, TGA, QOI, the Netpbm formats, TIFF, Radiance and OpenEXR, JPEG XL, ICO and
  CUR read and written; AVIF, JPEG 2000, JBIG2, CCITT fax and PSD read. See [Formats](articles/guides/formats.md).
- **Operations**: resizing and geometry, color management, tone and color, filters, compositing, masks, analysis and palettes.
- **More packages**: vector drawing (Egami.Drawing), fonts and text layout (Egami.Fonts), SVG (Egami.Svg), CSS (Egami.Css) and
  Windows metafiles (Egami.Metafiles). See [Packages](articles/packages.md).

```csharp
using Egami;

using Raster image = Raster.Load("photo.jpg");
image.Apply(ops => ops.AutoOrient().Fit(800, 800));
image.Save("photo-small.png");
```

Start with [Getting started](articles/getting-started.md), browse the [guides](articles/guides/formats.md) and the
[cookbook](articles/cookbook/index.md), or look anything up in the [API reference](api/Egami.yml).
