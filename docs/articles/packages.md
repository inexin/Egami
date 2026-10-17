# Packages

Egami is a family of packages. Each is fully managed C# with no dependencies outside the family, and you add only the ones you
use. Packages at 1.0 or above keep their public API compatible within the major version; packages at 0.x may still change.

| Package | Version | What it does | Needs | Guides |
| --- | --- | --- | --- | --- |
| Egami | 1.x | Rasters and pixel types, every format reader and writer, operations, color management, metadata, streaming decode | Nothing | [Getting started](getting-started.md), [Formats](guides/formats.md) and most guides |
| Egami.Drawing | 1.1.1 | Antialiased paths, shapes, strokes, clips, paints, layers, meshes and recorded drawings on a canvas | Egami, Egami.Fonts | [Drawing](guides/drawing.md), [The .egdr format](guides/egdr-format.md) |
| Egami.Fonts | 1.0 | TrueType and OpenType fonts, glyph outlines and metrics, subsetting, text shaping and paragraph layout | Egami | [Fonts and text](guides/text.md), [Unicode text and shaping](guides/unicode-text.md), [Paragraph layout](guides/paragraph-layout.md) |
| Egami.Svg | 0.1 | Reads SVG 1.1 and SVG 2 into an editable element tree, saves it back, and draws it onto any canvas | Egami, Egami.Drawing, Egami.Fonts, Egami.Css | [SVG](guides/svg.md) |
| Egami.Css | 0.2 | CSS syntax, selectors, the cascade, media queries and value parsers | Nothing | [CSS](guides/css.md) |
| Egami.Metafiles | 0.1 | Plays WMF, EMF and EMF+ pictures onto a canvas | Egami, Egami.Drawing, Egami.Fonts | [Windows metafiles](guides/metafiles.md) |

Egami.Svg goes to 1.0 once Egami.Css reaches 1.0.

The [changelog](https://github.com/inexin/Egami/blob/main/CHANGELOG.md) lists what each version adds.
