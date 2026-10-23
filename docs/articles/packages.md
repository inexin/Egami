# Packages

Egami is a family of packages. Each is fully managed C# with no dependencies outside the family, and you add only the ones you
use. Packages at 1.0 or above keep their public API compatible within the major version; packages at 0.x may still change.
Egami, Egami.Drawing, Egami.Fonts, Egami.Svg, Egami.Css, Egami.Heic, Egami.Cli and Egami.Cli.Host share one version and
release together; the other packages have versions of their own.

| Package | Version | What it does | Needs | Docs |
| --- | --- | --- | --- | --- |
| Egami | 1.x | Rasters and pixel types, every format reader and writer, operations, color management, metadata, streaming decode | Nothing | [Getting started](getting-started.md), [formats](formats/index.md), [all guides](guides/index.md) |
| Egami.Drawing | 1.x | Antialiased paths, shapes, strokes, clips, paints, layers, meshes and recorded drawings on a canvas | Egami, Egami.Fonts | [Drawing](guides/drawing.md), [The .egdr format](guides/egdr-format.md) |
| Egami.Fonts | 1.x | TrueType and OpenType fonts, glyph outlines and metrics, subsetting, text shaping and paragraph layout | Egami | [Fonts and text](guides/text.md), [Unicode text and shaping](guides/unicode-text.md), [Paragraph layout](guides/paragraph-layout.md) |
| Egami.Fonts.Substitutes | 1.0 | Bundled open fonts, metric-compatible with Arial, Helvetica, Times, Courier, Calibri and Cambria, for when those are not installed | Egami.Fonts | [Fonts and text](guides/text.md#substitute-fonts) |
| Egami.Svg | 1.x | Reads SVG 1.1 and SVG 2 into an editable element tree, saves it back, and draws it onto any canvas | Egami, Egami.Drawing, Egami.Fonts, Egami.Css | [SVG](guides/svg.md) |
| Egami.Css | 1.x | CSS syntax, selectors, the cascade, media queries and value parsers | Nothing | [CSS](guides/css.md) |
| Egami.Metafiles | 1.0 | Plays WMF, EMF and EMF+ pictures onto a canvas | Egami, Egami.Drawing, Egami.Fonts | [Windows metafiles](guides/metafiles.md) |
| Egami.Barcodes | 1.0 | Writes and reads 1D and 2D barcodes | Egami, Egami.Drawing, Egami.Fonts | [Barcodes](guides/barcodes.md), [Reading barcodes](guides/barcode-reading.md) |
| Egami.Video | 0.3 (preview) | Reads and writes video with Egami rasters as frames: WebM, Matroska, AVI, IVF and YUV4MPEG2; AV1, VP9, VP8 and Motion JPEG decode, Motion JPEG encode | Egami | [Video](guides/video.md) |
| Egami.Cli | 1.1 (in progress) | The `egami` command: convert, inspect, resize and edit images from a shell, chain steps, run workflow files and batches. A .NET tool, not a library, and the one package with a dependency outside the family: System.CommandLine | Egami, Egami.Drawing, Egami.Fonts, Egami.Fonts.Substitutes, Egami.Svg, Egami.Css, Egami.Metafiles | [The egami command](cli/index.md) |
| Egami.Cli.Host | 1.2 (in progress) | The command host of Egami's command-line tools as source: exit codes, globs, output templates, batches and an MCP server, compiled into a tool with internal types only. For tool authors, not for apps | System.CommandLine, ModelContextProtocol.Core | [Source on GitHub](https://github.com/inexin/Egami/tree/main/src/Egami.Cli.Host) |
| Egami.Heic | 1.1 (in progress) | Reads HEIC phone photos (HEVC-coded HEIF), opt-in because HEVC is covered by patents | Egami, the same version | [HEIC](formats/heic.md) |

The [changelog](https://github.com/inexin/Egami/blob/main/CHANGELOG.md) lists what each version adds.
