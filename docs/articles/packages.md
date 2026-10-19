# Packages

Egami is a family of packages. Each is fully managed C# with no dependencies outside the family, and you add only the ones you
use. Packages at 1.0 or above keep their public API compatible within the major version; packages at 0.x may still change.

| Package | Version | What it does | Needs | Guides |
| --- | --- | --- | --- | --- |
| Egami | 1.x | Rasters and pixel types, every format reader and writer, operations, color management, metadata, streaming decode | Nothing | [Getting started](getting-started.md), [Formats](guides/formats.md) and most guides |
| Egami.Drawing | 1.0 | Antialiased paths, shapes, strokes, clips, paints, layers, meshes and recorded drawings on a canvas | Egami, Egami.Fonts | [Drawing](guides/drawing.md), [The .egdr format](guides/egdr-format.md) |
| Egami.Fonts | 1.0 | TrueType and OpenType fonts, glyph outlines and metrics, subsetting, text shaping and paragraph layout | Egami | [Fonts and text](guides/text.md), [Unicode text and shaping](guides/unicode-text.md), [Paragraph layout](guides/paragraph-layout.md) |
| Egami.Fonts.Substitutes | 1.0 | Bundled open fonts, metric-compatible with Arial, Helvetica, Times, Courier, Calibri and Cambria, for when those are not installed | Egami.Fonts | [Fonts and text](guides/text.md#substitute-fonts) |
| Egami.Svg | 1.0 | Reads SVG 1.1 and SVG 2 into an editable element tree, saves it back, and draws it onto any canvas | Egami, Egami.Drawing, Egami.Fonts, Egami.Css | [SVG](guides/svg.md) |
| Egami.Css | 1.0 | CSS syntax, selectors, the cascade, media queries and value parsers | Nothing | [CSS](guides/css.md) |
| Egami.Metafiles | 1.0 | Plays WMF, EMF and EMF+ pictures onto a canvas | Egami, Egami.Drawing, Egami.Fonts | [Windows metafiles](guides/metafiles.md) |
| Egami.Barcodes | In development | Writes and reads 1D and 2D barcodes | Egami, Egami.Drawing, Egami.Fonts | [Barcodes](guides/barcodes.md) |
| Egami.Heic | 1.1 (in progress) | Reads HEIC phone photos (HEVC-coded HEIF), opt-in because HEVC is covered by patents | Egami, the same version | [HEIC](guides/heic.md) |

The [changelog](https://github.com/inexin/Egami/blob/main/CHANGELOG.md) lists what each version adds.
