# Extended formats

Egami.Formats.Extended reads and writes image formats that most apps never meet but some still need: PCX, SGI, Sun raster,
XBM, XPM, WBMP and Farbfeld. They are plugins on core Egami's public format API, the one [your own format](custom.md) uses,
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
| PCX | `PcxFormat` | 1, 2, 4 and 8-bit indexed, 1-bit EGA planes, 24-bit and 32-bit, run-length coded or not | 8-bit gray with a gray palette, or 24-bit RGB, run-length coded |
| SGI | `SgiFormat` | 1 to 4 channels of 8 or 16 bits, plain or run-length coded | the raster's channels and depth, run-length coded unless `SgiWriterOptions.Rle` is off |
| Sun raster | `SunRasterFormat` | 1, 8, 24 and 32 bits, with or without a colormap, plain or run-length coded | 8-bit gray or 24-bit, run-length coded when `SunRasterWriterOptions.Rle` is set |
| XBM | `XbmFormat` | X11 files of bytes and X10 files of 16-bit words | X11, black where gray is below half |
| XPM | `XpmFormat` | version 3, any number of characters a pixel, None, hex colors and X11 color names | every color of the raster, None below half alpha |
| WBMP | `WbmpFormat` | type 0, extension headers skipped | type 0, white where gray is at least half |
| Farbfeld | `FarbfeldFormat` | 16-bit RGBA | 16-bit RGBA |

SGI and Farbfeld keep 16-bit samples and alpha. PCX, Sun raster and WBMP have no alpha, XPM only on or off, and a save that
loses something the raster has says so in the Warnings, as Egami's own writers do. The formats with a run-length option take
it from their writer options:

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#options)]

What a header says that the pixels do not is kept in `RasterMetadata.FormatDetails`: `PcxDetails` (version, planes, palette),
`SgiDetails` (image name, channels, depth), `SunRasterDetails` (depth, colormap, coding), `XbmDetails` and `XpmDetails` (the
C name and the cursor hotspot). The SGI, XBM and XPM writers take the name and hotspot back from them.

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#details)]

## Streaming and damaged files

PCX, Sun raster, WBMP and Farbfeld decode their rows from the input as `RasterDecoder` asks for them; an 8-bit PCX first
reads to its end, where its palette is. SGI stores each channel as its own plane of bottom-up rows, and XBM and XPM are C
source, so those three read the file whole. A lenient read of a file that ends early keeps the rows it has and leaves the rest
transparent black, with a `RowsMissing` warning such as `Pcx.RowsMissing`; a strict read throws `CorruptDataException`.
