# Extended formats

Egami.Formats.Extended reads and writes image formats that most apps never meet but some still need: PCX, SGI, Sun raster,
XBM, XPM, WBMP, Farbfeld and DDS textures, and reads camera RAW files (DNG). They are plugins on core Egami's public format API, the one [your own format](custom.md) uses,
so they load, probe, stream and save as the core formats do, inside the same safety limits. The package depends on core
Egami only and ships with every Egami release.

## Registering the formats

Nothing registers them on their own. `WithExtendedFormats()`, an extension method in `ExtendedContextExtensions`, returns a
context with every stable format of the package, detected after core Egami's own; pass it to the calls, or make it the default
once at startup with `EgamiContext.SetDefault(new EgamiContext().WithExtendedFormats())`.

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#register)]

Each format also has its own method and its own `FormatInfo`, for an app that wants only one:

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#register-one)]

## The formats

| Format | `FormatInfo` | Reads | Writes |
| --- | --- | --- | --- |
| [PCX](pcx.md) | `PcxFormat` | 1, 2, 4 and 8-bit indexed, 1-bit EGA planes, 24-bit and 32-bit, run-length coded or not | 8-bit gray with a gray palette, or 24-bit RGB, run-length coded |
| [SGI](sgi.md) | `SgiFormat` | 1 to 4 channels of 8 or 16 bits, plain or run-length coded | the raster's channels and depth, run-length coded unless `SgiWriterOptions.Rle` is off |
| [Sun raster](sun-raster.md) | `SunRasterFormat` | 1, 8, 24 and 32 bits, with or without a colormap, plain or run-length coded | 8-bit gray or 24-bit, run-length coded when `SunRasterWriterOptions.Rle` is set |
| [XBM](xbm.md) | `XbmFormat` | X11 files of bytes and X10 files of 16-bit words | X11, black where gray is below half |
| [XPM](xpm.md) | `XpmFormat` | version 3, any number of characters a pixel, None, hex colors and X11 color names | every color of the raster, None below half alpha |
| [WBMP](wbmp.md) | `WbmpFormat` | type 0, extension headers skipped | type 0, white where gray is at least half |
| [Farbfeld](farbfeld.md) | `FarbfeldFormat` | 16-bit RGBA | 16-bit RGBA |
| [DDS](dds.md) | `DdsFormat` | Legacy and DX10 headers, BC1 to BC7, every DXGI format DirectXTex reads; every surface and mip through `DdsReaderOptions` | Uncompressed formats and BC1 to BC7 with mips, arrays and cube maps |
| [Camera RAW](raw.md) | `RawFormat` | DNG, developed on load or as its previews or mosaic, through `RawReaderOptions`; detected ahead of TIFF | — |

SGI and Farbfeld keep 16-bit samples and alpha. PCX, Sun raster and WBMP have no alpha, XPM only on or off, and a save that
loses something the raster has says so in the Warnings, as Egami's own writers do. The formats with a run-length option take
it from their writer options:

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#options)]

What a header says that the pixels do not is kept in `RasterMetadata.FormatDetails`: `PcxDetails` (version, planes, palette),
`SgiDetails` (image name, channels, depth), `SunRasterDetails` (depth, colormap, coding), `XbmDetails` and `XpmDetails` (the
C name and the cursor hotspot), `DdsDetails` (DXGI format, mips, cube and array layout, alpha mode), and `RawDetails` (camera, sensor layout, the develop's white balance and colour). The SGI, XBM and XPM writers take the name and hotspot back from them.

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#details)]

## Streaming and damaged files

PCX, Sun raster, WBMP and Farbfeld decode their rows from the input as `RasterDecoder` asks for them; an 8-bit PCX first
reads to its end, where its palette is. SGI stores each channel as its own plane of bottom-up rows, and XBM and XPM are C
source, so those three read the file whole. DDS skips to the surface it reads and decodes it a row of
4 × 4 blocks at a time. Camera RAW reads the file whole and develops the image before the first row. A lenient read of a file that ends early keeps the rows it has and leaves the rest
transparent black, with a `RowsMissing` warning such as `Pcx.RowsMissing`; a strict read throws `CorruptDataException`.

## Formats in preview

The package also holds formats still in development, as previews. A preview is never part of `WithExtendedFormats()`: its own
`With<Format>()` method and its `FormatInfo` carry .NET's `[Experimental]` attribute with a diagnostic id of its own, so a call
to it does not compile until the app suppresses that id, which is the opt-in. A preview may change, change its output or be
removed in any minor release; it becomes stable after an API review, comparison gates and fuzzing, and the attribute comes off.

The ids are `EGAMIX` and three digits, one per format, given out in order from `EGAMIX001` and never reused. Suppress one in the
project file (`<NoWarn>$(NoWarn);EGAMIX001</NoWarn>`) or around the call (`#pragma warning disable EGAMIX001`). There are no
formats in preview in this release. The `egami` command reads and writes them only with `--preview-formats`.
