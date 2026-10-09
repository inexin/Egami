# Egami

Egami ("image" backwards) is an open source 2D imaging library for .NET 10 that loads, edits and saves images.

- **MIT-licensed**, with no strings for commercial use.
- **Fully managed C#**: no native binaries, no P/Invoke, one package with no dependencies.
- **Safe on untrusted files**: sizes are checked before anything is allocated, and every reader is fuzz-tested.
- **Trimming and Native AOT clean**, on every OS and CPU .NET 10 supports.
- **Formats**: PNG and APNG, JPEG, GIF, BMP, TGA, QOI, PBM, PGM, PPM and PAM.

```csharp
using Egami;

using Raster image = Raster.Load("photo.jpg");
image.Apply(ops => ops.AutoOrient().Fit(800, 800));
image.Save("photo-small.png");
```

Start with [Getting started](articles/getting-started.md), browse the [guides](articles/guides/formats.md) and the
[cookbook](articles/cookbook/index.md), or look anything up in the [API reference](api/Egami.yml).
