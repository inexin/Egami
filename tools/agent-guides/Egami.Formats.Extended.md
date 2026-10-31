# Egami.Formats.Extended: guide for coding agents

Egami.Formats.Extended reads and writes image formats that most apps never meet but some still need: PCX, SGI, Sun raster, XBM,
XPM, WBMP, Farbfeld and DDS textures (BC1 to BC7, mips, cube maps, arrays and volumes), and reads camera RAW (DNG, CR2, NEF, PEF, ARW, ORF, RW2 and RAF), developed on
load. Each is a plugin on core Egami's public format API.

Namespaces: `Egami.Formats.Extended` (the `With...` methods), `Egami.Formats.Extended.<Format>` (`PcxFormat` and the other
format infos and options), with `Egami` and `Egami.Formats`.

{{shared}}

## Entry points

- `WithExtendedFormats()`: every format in a context; `WithPcx()`, `WithSgi()`, `WithSunRaster()`, `WithXbm()`, `WithXpm()`,
  `WithWbmp()`, `WithFarbfeld()`, `WithDds()` and `WithRaw()` one at a time.
- DDS: Load gives mip 0 of the first surface; `DdsReaderOptions` (`Surface`, `MipLevel`, `IncludeMipLevels`,
  `ReconstructNormalZ`) and LoadAll reach every face, array slice, depth slice and mip; `DdsDetails` keeps the DXGI format.
  Save writes a full mip chain; `DdsWriterOptions` (`Format`, `MipLevels`, `Effort`, `Srgb`, `Cube`) and SaveAll write
  block formats, arrays and cube maps.
- RAW: Load develops a DNG into `Rgb16` sRGB (or straight into the RGB space `ConvertColorTo` names); `RawReaderOptions`
  (`Output` = `Developed`, `HalfSize`, `Preview` or `Mosaic`; `WhiteBalance`, `Temperature`, `Tint`, `Exposure`, `Demosaic`,
  `Highlights`, `LuminanceNoiseReduction`, `ColorNoiseReduction`, `LensCorrections`, `ApplyProfile`) sets it; `RawDetails`
  keeps the camera, sensor layout and the colour the develop used.
- `EgamiContext.SetDefault(new EgamiContext().WithExtendedFormats())` at startup makes them work in every call.
- Load, Probe, streaming decodes, Save and Transcode then work as they do for core formats, inside the same safety limits.

## Samples

All of them:

{{sample FormatsExtended.cs#register}}

One at a time:

{{sample FormatsExtended.cs#register-one}}

DDS surfaces and mips:

{{sample FormatsExtended.cs#dds-surfaces}}

A RAW develop:

{{sample FormatsExtended.cs#raw-develop}}

## Common mistakes

- Referencing the package and expecting `.pcx` and the others to load. Register them in the context you load with, or as the
  default before any other Egami call.
- Saving to a path with a context that lacks them. `Save(path)` picks the writer through the raster's context, so a raster loaded
  without the formats cannot save as .pcx by extension; pass the format info (`PcxFormat.Info`) or load with the context.
- Expecting a DDS cube map or array to load whole with Load: it gives one surface. Use LoadAll, or `DdsReaderOptions.Surface`.
- Expecting BC5 normal maps to have blue: it loads as 0 unless `DdsReaderOptions.ReconstructNormalZ` is set.
- Expecting a RAW load to turn the image upright: like every Egami load it returns the pixels as stored; `AutoOrient` turns
  them by `RasterInfo.Orientation`.
- Registering RAW with `WithFormat(RawFormat.Info)`: that puts it after TIFF, which then claims every DNG. Use `WithRaw()`.
- Ignoring `Warnings` after a lenient read of a damaged file: it keeps what decoded and says what it recovered from.

## More

- Extended formats: https://inexin.github.io/Egami/v1/articles/formats/extended.html
- Every page in one list: https://inexin.github.io/Egami/v1/llms.txt
- API reference: https://inexin.github.io/Egami/v1/api/Egami.Formats.Extended.html
