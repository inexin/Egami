# ICO and CUR

Egami reads and writes Windows icons (.ico) and cursors (.cur), written from Microsoft's documentation of the icon directory.
`IcoFormat.Info` and `CurFormat.Info` are registered in `EgamiContext.Default`. An icon holds several images, its entries, each
a whole PNG file or a BMP without its file header; Egami's own PNG and BMP readers decode them, so an entry loads exactly as the
same PNG or BMP would. Every entry of the test corpus decodes as ImageMagick and Pillow decode it, and the icons Egami writes
open in ImageMagick, Pillow, SkiaSharp and Windows' own decoder (WIC) with the same pixels.

## Reading icons

`Raster.Load` returns the best entry: the largest, then the deepest (a PNG entry counts as 32 bits), then the first. SkiaSharp
picks the same entry, except that among entries of the largest size it takes the first where Egami takes the deepest, as Windows
prefers. `Raster.LoadAll` returns every entry at its own size, in the file's order, and `Raster.Probe` lists them from the
directory without decoding a pixel: `RasterInfo.ImageCount`, and `IcoDetails.Entries` on a loaded raster.

[!code-csharp[](../../../tests/Egami.DocSamples/Ico.cs#ico-load)]

`ReaderOptions.MinimumSize` picks the smallest entry at least that big before the [reduction rule](memory-and-threading.md#streaming)
applies to it, so a 32 × 32 request from a 16, 32, 48 and 256 icon decodes only the 32 × 32 entry. `IcoReaderOptions.Entry`
names an entry by its index in the directory.

[!code-csharp[](../../../tests/Egami.DocSamples/Ico.cs#ico-entry)]

[!code-csharp[](../../../tests/Egami.DocSamples/Ico.cs#ico-sizes)]

Every entry loads as `Rgba8`. BMP entries at 1, 4, 8, 16, 24 and 32 bits are read, with palettes and bit fields; the 1-bit AND
mask after the color rows makes pixels transparent. At 32 bits the alpha channel wins, as in Windows, except that an alpha channel
of 0 everywhere, which old writers left, falls back to the mask; ImageMagick, Pillow and SkiaSharp keep such an entry fully
transparent instead. The directory's sizes and depths are hints: the entry's own header wins, with the `Ico.DirectoryMismatch`
warning. A cursor's entries carry `IcoDetails.Hotspot`.

## Writing icons

Saving one raster writes one entry at its own size; a raster larger than 256 is fitted to 256 with the `Save.IcoFitted` warning.
`IcoWriterOptions.Sizes` writes a set instead, each size resized from the largest raster given with `Filter`, or taken as drawn
from a raster of exactly that size; `FaviconSizes` (16, 32, 48) and `WindowsSizes` (16, 24, 32, 48, 64, 256) are ready-made. A
raster that is not square is fitted inside each square and centred on transparency.

[!code-csharp[](../../../tests/Egami.DocSamples/Ico.cs#ico-save)]

`IcoWriterOptions.Encoding`, an `IcoEntryEncoding`, forces every entry to PNG or BMP. Left null, the default, it writes 256 × 256 entries as PNG and smaller ones as
32-bit BMP with an AND mask built from alpha, as Microsoft's guidance asks; a raster loaded from an icon and written at its own
size keeps its entry's kind. PNG entries are always 8-bit RGBA, the only kind Windows lists. Entries are sorted smallest first,
two entries of one size throw `ArgumentException`, and the same rasters and options give the same bytes. Pixels are written at
8 bits; an ICC profile goes into PNG entries, while EXIF, XMP and IPTC are not written and a warning says so.

Cursors are icons with a hotspot: `IcoWriterOptions.Hotspot` gives it at the raster's own size, scaled to each entry and kept
inside it; without it, a raster loaded from a cursor keeps its hotspot.

[!code-csharp[](../../../tests/Egami.DocSamples/Ico.cs#cur-save)]

## Speed

An icon is read whole, since its directory points anywhere in it. On the 256 × 256 PNG entry Egami decodes faster than SkiaSharp,
and 24 and 32-bit BMP entries go straight into place, twice as fast as SkiaSharp; writing Windows' six sizes from a 256 × 256
image takes half the time ImageMagick (Magick.NET) does.

## Damaged and hostile data

The entry count is checked against `SafetyLimits.MaxFrames` and every entry's size against the other limits before its pixels
are allocated; every offset and size is checked against the file. In strict mode an entry that lies outside the file, overlaps
another or is neither PNG nor BMP throws `CorruptDataException`; in lenient mode it is skipped with the `Ico.EntrySkipped`
warning, and the load fails only when no entry is left. A BMP entry that ends before its mask loads with the missing rows opaque
(`Ico.MaskMissing`). The reader is fuzzed with AFL++ through whole files and through single entries.
