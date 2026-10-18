# Egami.Metafiles

Egami.Metafiles plays Windows metafiles.

- Reads WMF, EMF and EMF+ (and WMZ and EMZ) and plays them onto Egami.Drawing's canvas, so they stay vectors in PDF and SVG
  output and rasterize on any OS.
- Draws them at a DPI or a size onto rasters, or stretched to a box on any canvas, as Word does.

See the [Windows metafiles guide](https://github.com/inexin/Egami/blob/main/docs/articles/guides/metafiles.md).

Egami is MIT-licensed, fully managed C# for .NET 10: no native binaries, no P/Invoke and no third-party dependencies, trimming
and Native AOT clean. Source, documentation and changelog: [github.com/inexin/Egami](https://github.com/inexin/Egami).
