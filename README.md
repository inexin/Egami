# Egami

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="branding/lockups/Egami-on-dark.svg">
  <img src="branding/lockups/Egami-on-light.svg" alt="Egami" height="64">
</picture>

[![NuGet](https://img.shields.io/nuget/v/Egami.svg)](https://www.nuget.org/packages/Egami)
[![CI](https://github.com/inexin/Egami/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/inexin/Egami/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Docs](https://img.shields.io/badge/docs-inexin.github.io%2FEgami-informational.svg)](https://inexin.github.io/Egami/)

Egami is an open source 2D imaging library for .NET 10 that loads, edits, draws and saves images. Around it sit packages for
text, SVG, barcodes, a System.Drawing drop-in, video and audio, and the `egami` command line, all in the same managed code.

- **MIT-licensed**, with no strings for commercial use.
- **Fully managed C#**: no native binaries, no P/Invoke, no third-party dependencies.
- **Safe on untrusted files**: size limits are checked before allocating, and every reader is fuzz-tested.
- **Trimming and Native AOT clean**, on every OS and CPU .NET 10 supports.

Egami is not a drop-in replacement for ImageSharp or SkiaSharp: it has its own API. The aim is to cover what they do, at the
same speed or faster: see the [benchmarks](https://inexin.github.io/Egami/v1/articles/benchmarks.html).

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

Add only the packages you use. Each one has its own guides, and its name links to its source code.

### Images

| Package | What it does | Docs | NuGet |
| --- | --- | --- | --- |
| [Egami](src/Egami) | Rasters and pixel types; PNG, JPEG, GIF, WebP, TIFF, JPEG XL, AVIF and more; resizing, color management, filters, metadata | [Getting started](https://inexin.github.io/Egami/v1/articles/getting-started.html), [formats](https://inexin.github.io/Egami/v1/articles/formats/index.html), [all guides](https://inexin.github.io/Egami/v1/articles/guides/index.html) | [![NuGet](https://img.shields.io/nuget/v/Egami.svg)](https://www.nuget.org/packages/Egami) |
| [Egami.Heic](src/Egami.Heic) | Reads HEIC phone photos; opt-in because HEVC is covered by patents | [Guide](https://inexin.github.io/Egami/v1/articles/formats/heic.html) | [![NuGet](https://img.shields.io/nuget/v/Egami.Heic.svg)](https://www.nuget.org/packages/Egami.Heic) |
| [Egami.Formats.Extended](src/Egami.Formats.Extended) | Extra image formats as plugins: PCX, SGI, Sun raster, XBM, XPM, WBMP and Farbfeld | [Guide](https://inexin.github.io/Egami/v1/articles/formats/extended.html) | [![NuGet](https://img.shields.io/nuget/v/Egami.Formats.Extended.svg)](https://www.nuget.org/packages/Egami.Formats.Extended) |

### Drawing and text

| Package | What it does | Docs | NuGet |
| --- | --- | --- | --- |
| [Egami.Drawing](src/Egami.Drawing) | Antialiased paths, shapes, text, gradients, layers and recorded drawings on a canvas | [Drawing](https://inexin.github.io/Egami/v1/articles/guides/drawing.html), [.egdr format](https://inexin.github.io/Egami/v1/articles/guides/egdr-format.html) | [![NuGet](https://img.shields.io/nuget/v/Egami.Drawing.svg)](https://www.nuget.org/packages/Egami.Drawing) |
| [Egami.Fonts](src/Egami.Fonts) | TrueType and OpenType fonts, text shaping and paragraph layout | [Fonts and text](https://inexin.github.io/Egami/v1/articles/guides/text.html), [Unicode text](https://inexin.github.io/Egami/v1/articles/guides/unicode-text.html), [Paragraph layout](https://inexin.github.io/Egami/v1/articles/guides/paragraph-layout.html) | [![NuGet](https://img.shields.io/nuget/v/Egami.Fonts.svg)](https://www.nuget.org/packages/Egami.Fonts) |
| [Egami.Fonts.Substitutes](src/Egami.Fonts.Substitutes) | Open fonts that stand in for Arial, Helvetica, Times, Courier, Calibri and Cambria | [Guide](https://inexin.github.io/Egami/v1/articles/guides/text.html#substitute-fonts) | [![NuGet](https://img.shields.io/nuget/v/Egami.Fonts.Substitutes.svg)](https://www.nuget.org/packages/Egami.Fonts.Substitutes) |
| [Egami.Barcodes](src/Egami.Barcodes) | Writes and reads 1D and 2D barcodes | [Writing](https://inexin.github.io/Egami/v1/articles/guides/barcodes.html), [reading](https://inexin.github.io/Egami/v1/articles/guides/barcode-reading.html) | [![NuGet](https://img.shields.io/nuget/v/Egami.Barcodes.svg)](https://www.nuget.org/packages/Egami.Barcodes) |

### Vector graphics

| Package | What it does | Docs | NuGet |
| --- | --- | --- | --- |
| [Egami.Svg](src/Egami.Svg) | Reads, edits, saves and draws SVG | [Guide](https://inexin.github.io/Egami/v1/articles/guides/svg.html) | [![NuGet](https://img.shields.io/nuget/v/Egami.Svg.svg)](https://www.nuget.org/packages/Egami.Svg) |
| [Egami.Css](src/Egami.Css) | CSS parsing, selectors and the cascade | [Guide](https://inexin.github.io/Egami/v1/articles/guides/css.html) | [![NuGet](https://img.shields.io/nuget/v/Egami.Css.svg)](https://www.nuget.org/packages/Egami.Css) |
| [Egami.Metafiles](src/Egami.Metafiles) | Plays WMF, EMF and EMF+ pictures | [Guide](https://inexin.github.io/Egami/v1/articles/guides/metafiles.html) | [![NuGet](https://img.shields.io/nuget/v/Egami.Metafiles.svg)](https://www.nuget.org/packages/Egami.Metafiles) |

### Compatibility

| Package | What it does | Docs | NuGet |
| --- | --- | --- | --- |
| [Egami.SystemDrawing](src/Egami.SystemDrawing) | System.Drawing.Common's API on every OS, in managed code: change the usings and keep the code (preview) | [System.Drawing drop-in](https://inexin.github.io/Egami/v1/articles/guides/system-drawing.html) | [![NuGet](https://img.shields.io/nuget/vpre/Egami.SystemDrawing.svg)](https://www.nuget.org/packages/Egami.SystemDrawing) |

### Video and audio

| Package | What it does | Docs | NuGet |
| --- | --- | --- | --- |
| [Egami.Video](src/Egami.Video) | Reads and writes video and audio with Egami rasters as frames: MP4, WebM, Matroska, AVI; AV1, VP9 and VP8 decode and encode; Opus, Vorbis, FLAC, MP3 and more | [Guide](https://inexin.github.io/Egami/v1/articles/guides/video.html) | [![NuGet](https://img.shields.io/nuget/v/Egami.Video.svg)](https://www.nuget.org/packages/Egami.Video) |
| [Egami.Video.H264](src/Egami.Video.H264) | H.264 decoding for Egami.Video; opt-in because H.264 is covered by patents | [Guide](https://inexin.github.io/Egami/v1/articles/guides/h264.html) | [![NuGet](https://img.shields.io/nuget/v/Egami.Video.H264.svg)](https://www.nuget.org/packages/Egami.Video.H264) |

### Command-line tools

| Package | What it does | Docs | NuGet |
| --- | --- | --- | --- |
| [Egami.Cli](src/Egami.Cli) | The `egami` command: convert, resize, inspect and edit images from a shell, with chained steps, workflow files and batches | [The egami command](https://inexin.github.io/Egami/v1/articles/cli/index.html) | [![NuGet](https://img.shields.io/nuget/v/Egami.Cli.svg)](https://www.nuget.org/packages/Egami.Cli) |
| [Egami.Video.Cli](src/Egami.Video.Cli) | The `egami-video` command: inspect, remux, cut, join and convert video and audio from a shell, with ffmpeg and ffprobe command lines | [The egami-video command](https://inexin.github.io/Egami/v1/articles/video-cli/index.html) | [![NuGet](https://img.shields.io/nuget/v/Egami.Video.Cli.svg)](https://www.nuget.org/packages/Egami.Video.Cli) |

All packages except Egami.SystemDrawing (a preview) are 1.0 or later, and all are on [NuGet](https://www.nuget.org/packages?q=Egami).
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
