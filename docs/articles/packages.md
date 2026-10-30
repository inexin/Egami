# Packages

Egami is a family of packages. Each is fully managed C# with no dependencies outside the family, and you add only the ones you
use. Packages at 1.0 or above keep their public API compatible within the major version; packages at 0.x may still change.
Egami, Egami.Drawing, Egami.Fonts, Egami.Svg, Egami.Css, Egami.Heic, Egami.Formats.Extended, Egami.Cli and Egami.Cli.Host share
one version and release together; the other packages have versions of their own. Each package name links to its source code.

## Images

| Package | Version | What it does | Needs | Docs |
| --- | --- | --- | --- | --- |
| [Egami](https://github.com/inexin/Egami/tree/main/src/Egami) | 1.x | Rasters and pixel types, every format reader and writer, operations, color management, metadata, streaming decode | Nothing | [Getting started](getting-started.md), [formats](formats/index.md), [all guides](guides/index.md) |
| [Egami.Heic](https://github.com/inexin/Egami/tree/main/src/Egami.Heic) | 1.x | Reads HEIC phone photos (HEVC-coded HEIF), opt-in because HEVC is covered by patents | Egami, the same version | [HEIC](formats/heic.md) |
| [Egami.Formats.Extended](https://github.com/inexin/Egami/tree/main/src/Egami.Formats.Extended) | 1.x | Extra image formats as plugins on core Egami's format API: PCX, SGI, Sun raster, XBM, XPM, WBMP and Farbfeld, read and written | Egami, the same version | [Extended formats](formats/extended.md) |

## Drawing and text

| Package | Version | What it does | Needs | Docs |
| --- | --- | --- | --- | --- |
| [Egami.Drawing](https://github.com/inexin/Egami/tree/main/src/Egami.Drawing) | 1.x | Antialiased paths, shapes, strokes, clips, paints, layers, meshes and recorded drawings on a canvas | Egami, Egami.Fonts | [Drawing](guides/drawing.md), [The .egdr format](guides/egdr-format.md) |
| [Egami.Fonts](https://github.com/inexin/Egami/tree/main/src/Egami.Fonts) | 1.x | TrueType and OpenType fonts, glyph outlines and metrics, subsetting, text shaping and paragraph layout | Egami | [Fonts and text](guides/text.md), [Unicode text and shaping](guides/unicode-text.md), [Paragraph layout](guides/paragraph-layout.md) |
| [Egami.Fonts.Substitutes](https://github.com/inexin/Egami/tree/main/src/Egami.Fonts.Substitutes) | 1.0 | Bundled open fonts, metric-compatible with Arial, Helvetica, Times, Courier, Calibri and Cambria, for when those are not installed | Egami.Fonts | [Fonts and text](guides/text.md#substitute-fonts) |
| [Egami.Barcodes](https://github.com/inexin/Egami/tree/main/src/Egami.Barcodes) | 1.0 | Writes and reads 1D and 2D barcodes | Egami, Egami.Drawing, Egami.Fonts | [Barcodes](guides/barcodes.md), [Reading barcodes](guides/barcode-reading.md) |

## Vector graphics

| Package | Version | What it does | Needs | Docs |
| --- | --- | --- | --- | --- |
| [Egami.Svg](https://github.com/inexin/Egami/tree/main/src/Egami.Svg) | 1.x | Reads SVG 1.1 and SVG 2 into an editable element tree, saves it back, and draws it onto any canvas | Egami, Egami.Drawing, Egami.Fonts, Egami.Css | [SVG](guides/svg.md) |
| [Egami.Css](https://github.com/inexin/Egami/tree/main/src/Egami.Css) | 1.x | CSS syntax, selectors, the cascade, media queries and value parsers | Nothing | [CSS](guides/css.md) |
| [Egami.Metafiles](https://github.com/inexin/Egami/tree/main/src/Egami.Metafiles) | 1.0 | Plays WMF, EMF and EMF+ pictures onto a canvas | Egami, Egami.Drawing, Egami.Fonts | [Windows metafiles](guides/metafiles.md) |

## Compatibility

| Package | Version | What it does | Needs | Docs |
| --- | --- | --- | --- | --- |
| [Egami.SystemDrawing](https://github.com/inexin/Egami/tree/main/src/Egami.SystemDrawing) | 1.0 preview | System.Drawing.Common's API on every OS, in managed code: bitmaps, codecs, Graphics, pens, brushes, paths, fonts and text, metafiles and effects | Egami, Egami.Drawing, Egami.Fonts, Egami.Metafiles | [System.Drawing drop-in](guides/system-drawing.md) |

## Video and audio

| Package | Version | What it does | Needs | Docs |
| --- | --- | --- | --- | --- |
| [Egami.Video](https://github.com/inexin/Egami/tree/main/src/Egami.Video) | 1.1 | Reads and writes video and audio with Egami rasters as frames: MP4, WebM, Matroska, AVI, IVF, YUV4MPEG2, Ogg, WAV, FLAC, MP3, AIFF and CAF; AV1, VP9 and VP8 decode and encode, Motion JPEG, Opus, Vorbis, FLAC, MP3, AC-3 and ALAC; subtitles, smart cut and scene detection | Egami | [Video](guides/video.md) |
| [Egami.Video.H264](https://github.com/inexin/Egami/tree/main/src/Egami.Video.H264) | 1.1 | H.264 (AVC) decoding for Egami.Video: progressive Constrained Baseline, Main, High, High 10 and High 4:2:2 in MP4, MOV, Matroska, AVI and raw .h264 files. Opt-in because H.264 is covered by patents | Egami.Video, the same version | [H.264](guides/h264.md) |
| [Egami.Video.Hevc](https://github.com/inexin/Egami/tree/main/src/Egami.Video.Hevc) | unreleased | HEVC (H.265) decoding for Egami.Video: Main, Main 10, Main 12 and the range extensions to 12 bits in MP4, MOV, Matroska and raw .h265 files, through Egami.Heic's decoder. Opt-in because HEVC is covered by patents | Egami.Video, the same version, and Egami.Heic | [HEVC](guides/hevc.md) |

## Command-line tools

| Package | Version | What it does | Needs | Docs |
| --- | --- | --- | --- | --- |
| [Egami.Cli](https://github.com/inexin/Egami/tree/main/src/Egami.Cli) | 1.x | The `egami` command: convert, inspect, resize and edit images from a shell, chain steps, run workflow files and batches. A .NET tool, not a library, and the one package with a dependency outside the family: System.CommandLine | Egami, Egami.Drawing, Egami.Fonts, Egami.Fonts.Substitutes, Egami.Svg, Egami.Css, Egami.Metafiles, Egami.Formats.Extended | [The egami command](cli/index.md) |
| [Egami.Video.Cli](https://github.com/inexin/Egami/tree/main/src/Egami.Video.Cli) | 1.1 | The `egami-video` command: inspect, remux, cut, join, export frames, make clips and convert audio from a shell, with ffmpeg and ffprobe command lines and an MCP server. A .NET tool that carries Egami.Video's version | Egami.Video, Egami, Egami.Drawing, Egami.Fonts, Egami.Fonts.Substitutes, Egami.Svg, Egami.Metafiles, Egami.Formats.Extended, System.CommandLine, ModelContextProtocol.Core | [The egami-video command](video-cli/index.md) |
| [Egami.Cli.Host](https://github.com/inexin/Egami/tree/main/src/Egami.Cli.Host) | 1.x | The command host of Egami's command-line tools as source: exit codes, globs, output templates, batches and an MCP server, compiled into a tool with internal types only. For tool authors, not for apps | System.CommandLine, ModelContextProtocol.Core | [Source on GitHub](https://github.com/inexin/Egami/tree/main/src/Egami.Cli.Host) |

The [changelog](https://github.com/inexin/Egami/blob/main/CHANGELOG.md) lists what each version adds.
