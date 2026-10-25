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

Add only the packages you use. Each one has its own guides.

| Package | What it does | Docs |
| --- | --- | --- |
| [Egami](https://www.nuget.org/packages/Egami) | Rasters and pixel types; PNG, JPEG, GIF, WebP, TIFF, JPEG XL, AVIF and more; resizing, color management, filters, metadata | [Getting started](https://inexin.github.io/Egami/v1/articles/getting-started.html), [formats](https://inexin.github.io/Egami/v1/articles/formats/index.html), [all guides](https://inexin.github.io/Egami/v1/articles/guides/index.html) |
| [Egami.Drawing](https://www.nuget.org/packages/Egami.Drawing) | Antialiased paths, shapes, text, gradients, layers and recorded drawings on a canvas | [Drawing](https://inexin.github.io/Egami/v1/articles/guides/drawing.html), [.egdr format](https://inexin.github.io/Egami/v1/articles/guides/egdr-format.html) |
| [Egami.Fonts](https://www.nuget.org/packages/Egami.Fonts) | TrueType and OpenType fonts, text shaping and paragraph layout | [Fonts and text](https://inexin.github.io/Egami/v1/articles/guides/text.html), [Unicode text](https://inexin.github.io/Egami/v1/articles/guides/unicode-text.html), [Paragraph layout](https://inexin.github.io/Egami/v1/articles/guides/paragraph-layout.html) |
| [Egami.Fonts.Substitutes](https://www.nuget.org/packages/Egami.Fonts.Substitutes) | Open fonts that stand in for Arial, Helvetica, Times, Courier, Calibri and Cambria | [Guide](https://inexin.github.io/Egami/v1/articles/guides/text.html#substitute-fonts) |
| [Egami.Svg](https://www.nuget.org/packages/Egami.Svg) | Reads, edits, saves and draws SVG | [Guide](https://inexin.github.io/Egami/v1/articles/guides/svg.html) |
| [Egami.Css](https://www.nuget.org/packages/Egami.Css) | CSS parsing, selectors and the cascade | [Guide](https://inexin.github.io/Egami/v1/articles/guides/css.html) |
| [Egami.Metafiles](https://www.nuget.org/packages/Egami.Metafiles) | Plays WMF, EMF and EMF+ pictures | [Guide](https://inexin.github.io/Egami/v1/articles/guides/metafiles.html) |
| [Egami.Barcodes](https://www.nuget.org/packages/Egami.Barcodes) | Writes and reads 1D and 2D barcodes | [Writing](https://inexin.github.io/Egami/v1/articles/guides/barcodes.html), [reading](https://inexin.github.io/Egami/v1/articles/guides/barcode-reading.html) |
| [Egami.Cli](https://www.nuget.org/packages/Egami.Cli) | The `egami` command: convert, resize, inspect and edit images from a shell, with chained steps, workflow files and batches | [The egami command](https://inexin.github.io/Egami/v1/articles/cli/index.html) |
| [Egami.Video](https://www.nuget.org/packages/Egami.Video) | Reads and writes video and audio with Egami rasters as frames: MP4, WebM, Matroska, AVI; AV1, VP9 and VP8 decode and encode; Opus, Vorbis, FLAC, MP3 and more | [Guide](https://inexin.github.io/Egami/v1/articles/guides/video.html) |
| [Egami.Video.H264](https://www.nuget.org/packages/Egami.Video.H264) | H.264 decoding for Egami.Video; opt-in because H.264 is covered by patents | [Guide](https://inexin.github.io/Egami/v1/articles/guides/h264.html) |
| [Egami.Video.Cli](https://www.nuget.org/packages/Egami.Video.Cli) | The `egami-video` command: inspect, remux, cut, join and convert video and audio from a shell, with ffmpeg and ffprobe command lines | [The egami-video command](https://inexin.github.io/Egami/v1/articles/video-cli/index.html) |
| [Egami.Heic](https://www.nuget.org/packages/Egami.Heic) | Reads HEIC phone photos; opt-in because HEVC is covered by patents | [Guide](https://inexin.github.io/Egami/v1/articles/formats/heic.html) |

All packages are 1.0 or later and on [NuGet](https://www.nuget.org/packages?q=Egami).
[Packages](https://inexin.github.io/Egami/v1/articles/packages.html) lists what each one needs.

## The egami command

Egami.Cli is a .NET tool that brings Egami to the shell and to build scripts:

```sh
dotnet tool install --global Egami.Cli
egami info photo.jpg
egami thumbnail "photos/*.jpg" -o thumbs/{name}.webp --size 256
egami run in.jpg auto-orient + resize 1200x + sharpen 0.6 + save out.webp --quality 82
```

[The egami command](https://inexin.github.io/Egami/v1/articles/cli/index.html) explains its commands, steps, workflow files and
batches. `egami mcp` serves the same commands to AI assistants such as Claude and GitHub Copilot over the Model Context Protocol,
inside the folders you allow: see [egami for AI assistants](https://inexin.github.io/Egami/v1/articles/cli/assistants.html).

## Documentation

The documentation is at [inexin.github.io/Egami](https://inexin.github.io/Egami/):

- **[Getting started](https://inexin.github.io/Egami/v1/articles/getting-started.html)** loads, edits and saves a first image.
- **[Formats](https://inexin.github.io/Egami/v1/articles/formats/index.html)** has a page for each file format, with its options.
- **The [guides](https://inexin.github.io/Egami/v1/articles/guides/index.html)** explain one area each: what it does, the options, the defaults and the
  limits. They are grouped by package and go from the basics to the specialized, and many end with recipes for common tasks.
- **The [API reference](https://inexin.github.io/Egami/v1/api/Egami.html)** lists every type and member.

What each version adds is in the [changelog](CHANGELOG.md).

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for building, testing and the clean-room rules. Security issues go through private
reporting; see [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE)
