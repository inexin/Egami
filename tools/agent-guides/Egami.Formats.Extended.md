# Egami.Formats.Extended: guide for coding agents

Egami.Formats.Extended reads and writes image formats that most apps never meet but some still need: PCX, SGI, Sun raster, XBM,
XPM, WBMP and Farbfeld. Each is a plugin on core Egami's public format API.

Namespaces: `Egami.Formats.Extended` (the `With...` methods), `Egami.Formats.Extended.<Format>` (`PcxFormat` and the other
format infos and options), with `Egami` and `Egami.Formats`.

{{shared}}

## Entry points

- `WithExtendedFormats()`: every format in a context; `WithPcx()`, `WithSgi()`, `WithSunRaster()`, `WithXbm()`, `WithXpm()`,
  `WithWbmp()` and `WithFarbfeld()` one at a time.
- `EgamiContext.SetDefault(new EgamiContext().WithExtendedFormats())` at startup makes them work in every call.
- Load, Probe, streaming decodes, Save and Transcode then work as they do for core formats, inside the same safety limits.

## Samples

All of them:

{{sample FormatsExtended.cs#register}}

One at a time:

{{sample FormatsExtended.cs#register-one}}

## Common mistakes

- Referencing the package and expecting `.pcx` and the others to load. Register them in the context you load with, or as the
  default before any other Egami call.
- Saving to a path with a context that lacks them. `Save(path)` picks the writer through the raster's context, so a raster loaded
  without the formats cannot save as .pcx by extension; pass the format info (`PcxFormat.Info`) or load with the context.
- Ignoring `Warnings` after a lenient read of a damaged file: it keeps what decoded and says what it recovered from.

## More

- Extended formats: https://inexin.github.io/Egami/v1/articles/formats/extended.html
- Every page in one list: https://inexin.github.io/Egami/v1/llms.txt
- API reference: https://inexin.github.io/Egami/v1/api/Egami.Formats.Extended.html
