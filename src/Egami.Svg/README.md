# Egami.Svg

Egami.Svg reads, edits, saves and draws SVG.

- Loads SVG 1.1 and SVG 2 (and SVGZ) into an editable element tree and saves it back.
- Draws through Egami.Drawing's canvas onto rasters, PDF pages or SVG, with styles, text, filters and color fonts.
- Lets `Raster.Load` read SVG files, safely: nothing outside the file is opened unless you allow it.

See the [SVG guide](https://github.com/inexin/Egami/blob/main/docs/articles/guides/svg.md).

Egami is MIT-licensed, fully managed C# for .NET 10: no native binaries, no P/Invoke and no third-party dependencies, trimming
and Native AOT clean. Source, documentation and changelog: [github.com/inexin/Egami](https://github.com/inexin/Egami).
