# Egami

Egami loads, edits and saves images in .NET 10.

- Reads and writes PNG and APNG, JPEG, GIF, WebP, BMP, TGA, QOI, the Netpbm formats, TIFF, Radiance and OpenEXR, JPEG XL, ICO
  and CUR; reads AVIF, JPEG 2000, JBIG2, CCITT fax and PSD.
- Resizing and geometry, color management, tone and color, filters, compositing, masks, analysis and palettes.
- Safe on untrusted files: size limits are checked before anything is allocated, and every reader is fuzz-tested.

```csharp
using Egami;

using Raster image = Raster.Load("photo.jpg");
image.Apply(ops => ops.AutoOrient().Fit(800, 800));
image.Save("photo-small.png");
```

Start with [Getting started](https://github.com/inexin/Egami/blob/main/docs/articles/getting-started.md) and the [guides](https://github.com/inexin/Egami/blob/main/docs/articles/guides/formats.md).

Egami is MIT-licensed, fully managed C# for .NET 10: no native binaries, no P/Invoke and no third-party dependencies, trimming
and Native AOT clean. Source, documentation and changelog: [github.com/inexin/Egami](https://github.com/inexin/Egami).
