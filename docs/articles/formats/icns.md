# ICNS

Egami reads and writes Apple icon families (.icns), written from Apple's description of the format. `IcnsFormat.Info` is
registered in `EgamiContext.Default`. An icon family holds several images, its entries, each in an element named by a
four-character type: a whole PNG or JPEG 2000 file (`ic07` to `ic14`, `icp4` to `icp6`), run-length encoded ARGB (`ic04`,
`ic05`), run-length encoded RGB with an 8-bit mask (`is32`, `il32`, `ih32`, `it32` with `s8mk`, `l8mk`, `h8mk`, `t8mk`), or
the old 1, 4 and 8-bit icons with a 1-bit mask (`ICN#`, `icl4`, `icl8` and their small and huge sizes). Egami's own PNG and
JPEG 2000 readers decode those entries, so an entry loads exactly as the same file would. PNG entries and RGB entries with
their masks decode as Pillow decodes them, and Pillow reads the icons Egami writes with the same pixels.

## Reading icons

`Raster.Load` returns the best entry: the largest, then the deepest, then the first. `Raster.LoadAll` returns every entry at
its own size, in the file's order, and `Raster.Probe` counts them from the elements without decoding a pixel:
`RasterInfo.ImageCount`, and `IcnsDetails.Entries` on a loaded raster, with each entry's type, size, depth and
`IcnsEntryEncoding`. Masks, the table of contents and other elements are not entries.

[!code-csharp[](../../../tests/Egami.DocSamples/Icns.cs#icns-load)]

`ReaderOptions.MinimumSize` picks the smallest entry at least that big before the [reduction rule](../guides/memory-and-threading.md#streaming)
applies to it. `IcnsReaderOptions.Entry` names an entry by its index in `IcnsDetails.Entries`.

[!code-csharp[](../../../tests/Egami.DocSamples/Icns.cs#icns-entry)]

[!code-csharp[](../../../tests/Egami.DocSamples/Icns.cs#icns-sizes)]

Every entry loads as `Rgba8`. An RGB entry without its 8-bit mask takes alpha from the 1-bit mask of its size, or is opaque.
The 4 and 8-bit icons use the Mac OS system palettes, and 1 bits are black. A PNG entry's own size wins over its type's. An
element whose length runs past the end of the file ends the list there with the `Icns.ElementCut` warning, and an entry that
does not decode is skipped for the next best with `Icns.EntryDamaged`; in strict mode both throw.

## Writing icons

Saving one raster writes one PNG entry at the largest of the entry sizes (16, 32, 64, 128, 256, 512 and 1024 square) that it
fills; a raster of another size is fitted to it with the `Save.IcnsFitted` warning. `IcnsWriterOptions.Sizes` writes a set
instead, each size resized from the largest raster given with `Filter`, or taken as drawn from a raster of exactly that size;
`MacSizes` holds all seven, which macOS needs at 1× and 2× for every point size. A raster that is not square is fitted inside
each square and centred on transparency.

[!code-csharp[](../../../tests/Egami.DocSamples/Icns.cs#icns-save)]

Each size is written once, under the type of its pixel size (`icp4`, `icp5`, `icp6`, `ic07`, `ic08`, `ic09`, `ic10`), after a
`TOC ` element that lists them, as macOS writes it. Entries are 8-bit RGBA PNG, sorted smallest first; two entries of one
size throw `ArgumentException`, and the same rasters and options give the same bytes. An ICC profile goes into the PNG
entries, while EXIF, XMP and IPTC are not written and a warning says so.
