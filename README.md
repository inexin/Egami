# Egami

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="branding/lockups/Egami-on-dark.svg">
  <img src="branding/lockups/Egami-on-light.svg" alt="Egami" height="64">
</picture>

Egami is an open source 2D imaging library for .NET 10 that loads, edits, draws and saves images.

- **MIT-licensed**, with no strings for commercial use.
- **Fully managed C#**: no native binaries, no P/Invoke, no third-party dependencies.
- **Safe on untrusted files**: size limits are checked before allocating, and every reader is fuzz-tested.
- **Trimming and Native AOT clean**, on every OS and CPU .NET 10 supports.

Egami is not a drop-in replacement for ImageSharp or SkiaSharp: it has its own API. The aim is to cover what they do, at the
same speed or faster.

```sh
dotnet add package Egami
```

```csharp
using Egami;

using Raster image = Raster.Load("photo.jpg");
image.Apply(ops => ops.AutoOrient().Fit(800, 800));
image.Save("photo-small.png");
```

## Packages

| Package | What it does |
| --- | --- |
| Egami | Rasters and pixel types; PNG, JPEG, GIF, WebP, TIFF, JPEG XL, AVIF and more; resizing, color management, filters, metadata |
| Egami.Drawing | Antialiased paths, shapes, text, gradients, layers and recorded drawings on a canvas |
| Egami.Fonts | TrueType and OpenType fonts, text shaping and paragraph layout |
| Egami.Svg | Reads, edits, saves and draws SVG |
| Egami.Css | CSS parsing, selectors and the cascade |
| Egami.Metafiles | Plays WMF, EMF and EMF+ pictures |

Egami, Egami.Drawing, Egami.Fonts, Egami.Svg and Egami.Css 1.0.0 are on [NuGet](https://www.nuget.org/packages?q=Egami);
Egami.Metafiles is not published yet.

## Documentation

The documentation is at [inexin.github.io/Egami](https://inexin.github.io/Egami/): [Getting started](https://inexin.github.io/Egami/v1/articles/getting-started.html),
the [guides](https://inexin.github.io/Egami/v1/articles/guides/formats.html), the
[cookbook](https://inexin.github.io/Egami/v1/articles/cookbook/index.html) and the
[API reference](https://inexin.github.io/Egami/v1/api/Egami.html). What each version adds is in the [changelog](CHANGELOG.md).

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for building, testing and the clean-room rules. Security issues go through private
reporting; see [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE)
